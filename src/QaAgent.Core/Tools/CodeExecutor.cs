using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Tools;

/// <summary>
/// Compiles generated C# test code in-memory using Roslyn,
/// then executes each [Fact] method and returns TestResult objects.
/// No file system or external test runner needed.
/// </summary>
public sealed class CodeExecutor
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Fields
    // ─────────────────────────────────────────────────────────────────────────

    private readonly TimeSpan _testTimeout;
    private readonly int      _maxParallelMethods;

    // References needed to compile xUnit-style test code
    private static readonly IReadOnlyList<MetadataReference> BaseReferences = BuildBaseReferences();

    // ─────────────────────────────────────────────────────────────────────────
    //  Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public CodeExecutor(TimeSpan? testTimeout = null, int maxParallelMethods = 5)
    {
        _testTimeout         = testTimeout ?? TimeSpan.FromSeconds(30);
        _maxParallelMethods  = maxParallelMethods;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compiles and runs all test methods in the given TestCase's GeneratedCode.
    /// Returns one TestResult per [Fact] method found in the class.
    /// </summary>
    public async Task<List<TestResult>> ExecuteAsync(
        TestCase testCase,
        CancellationToken ct = default)
    {
        if (testCase.GeneratedCode is null)
        {
            return [CreateSkipped(testCase, "No generated code available.")];
        }

        Log($"Compiling: {testCase.Endpoint.Label}...");

        // Step 1: Compile
        var compileResult = Compile(testCase.GeneratedCode);
        if (!compileResult.Success)
        {
            var errors = string.Join("\n", compileResult.Errors);
            Log($"  [COMPILE ERROR] {errors}");

            return [new TestResult
            {
                TestCase = testCase,
                Status = TestStatus.Error,
                ErrorMessage = $"Compilation failed:\n{errors}",
                Duration = TimeSpan.Zero
            }];
        }

        Log($"  Compiled OK. Running test methods...");

        // Step 2: Find and run all [Fact] methods
        var testMethods = FindTestMethods(compileResult.Assembly!);

        if (testMethods.Count == 0)
        {
            return [CreateSkipped(testCase, "No [Fact] test methods found in generated code.")];
        }

        // Виконуємо тест-методи ПАРАЛЕЛЬНО — кожен створює свій HttpClient
        // SemaphoreSlim обмежує кількість одночасних HTTP-запитів
        using var httpSemaphore = new SemaphoreSlim(_maxParallelMethods, _maxParallelMethods);

        var tasks = testMethods.Select(async tm =>
        {
            await httpSemaphore.WaitAsync(ct);
            try   { return await RunMethodAsync(testCase, tm.TypeName, tm.MethodName, tm.Method, ct); }
            finally { httpSemaphore.Release(); }
        });

        var results = (await Task.WhenAll(tasks)).ToList();

        var passed     = results.Count(r => r.Status == TestStatus.Passed);
        var failed     = results.Count(r => r.Status == TestStatus.Failed);
        var errorCount = results.Count(r => r.Status == TestStatus.Error);
        Log($"  Results: {passed} passed, {failed} failed, {errorCount} errors");

        return results;
    }

    /// <summary>
    /// Compiles and runs all test cases. Returns all results flat.
    /// </summary>
    public async Task<List<TestResult>> ExecuteAllAsync(
        IReadOnlyList<TestCase> testCases,
        CancellationToken ct = default)
    {
        // Deduplicate by generated code — same class shouldn't run twice
        var seen = new HashSet<string>();
        var results = new List<TestResult>();

        foreach (var tc in testCases)
        {
            var key = tc.GeneratedCode ?? tc.Id;
            if (!seen.Add(key)) continue;
            var tcResults = await ExecuteAsync(tc, ct);
            results.AddRange(tcResults);
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Compilation
    // ─────────────────────────────────────────────────────────────────────────

    // Global usings prepended to every generated file so the LLM does not need
    // to produce them — handles Uri, Task, List<>, HttpClient, Assert, etc.
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Net;
        global using System.Net.Http;
        global using System.Net.Http.Headers;
        global using System.Net.Http.Json;
        global using System.Text;
        global using System.Text.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Xunit;
        """;

    private static CompileResult Compile(string sourceCode)
    {
        // Prepend global usings so generated code always has full access
        var fullSource = GlobalUsings + Environment.NewLine + sourceCode;

        var syntaxTree = CSharpSyntaxTree.ParseText(fullSource);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"QaTests_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: BaseReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithOptimizationLevel(OptimizationLevel.Debug));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"L{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
                .ToList();

            return new CompileResult(false, null, errors);
        }

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        return new CompileResult(true, assembly, []);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Test method discovery
    // ─────────────────────────────────────────────────────────────────────────

    private record TestMethodInfo(string TypeName, string MethodName, MethodInfo Method);

    private static List<TestMethodInfo> FindTestMethods(Assembly assembly)
    {
        var results = new List<TestMethodInfo>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var hasFactAttr = method.GetCustomAttributesData()
                    .Any(a => a.AttributeType.Name == "FactAttribute");

                var isTestMethod = hasFactAttr ||
                    method.Name.StartsWith("Test", StringComparison.OrdinalIgnoreCase);

                if (isTestMethod && method.ReturnType == typeof(Task))
                    results.Add(new TestMethodInfo(type.FullName ?? type.Name, method.Name, method));
            }
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Test execution
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<TestResult> RunMethodAsync(
        TestCase testCase,
        string _,
        string methodName,
        MethodInfo method,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Create a per-test TestCase with the method name as scenario
        var specificTestCase = new TestCase
        {
            Endpoint = testCase.Endpoint,
            ScenarioName = methodName,
            ScenarioType = testCase.ScenarioType,
            ExpectedStatusCode = testCase.ExpectedStatusCode,
            GeneratedCode = testCase.GeneratedCode
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_testTimeout);

            // Instantiate the test class
            var instance = Activator.CreateInstance(method.DeclaringType!);

            // Invoke the async test method
            var task = (Task)method.Invoke(instance, null)!;
            await task.WaitAsync(cts.Token);

            sw.Stop();
            Log($"    ✅ {methodName} — PASSED ({sw.ElapsedMilliseconds}ms)");

            return new TestResult
            {
                TestCase = specificTestCase,
                Status = TestStatus.Passed,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log($"    ⏱ {methodName} — TIMEOUT");

            return new TestResult
            {
                TestCase = specificTestCase,
                Status = TestStatus.Error,
                ErrorMessage = $"Test timed out after {_testTimeout.TotalSeconds}s",
                Duration = sw.Elapsed
            };
        }
        catch (Exception caughtEx)
        {
            sw.Stop();

            // Unwrap TargetInvocationException if present
            var inner = caughtEx is TargetInvocationException tie
                ? tie.InnerException ?? tie
                : caughtEx;

            // Walk the exception hierarchy to detect xUnit assertion failures
            static bool IsXunitException(Exception ex)
            {
                var t = ex.GetType();
                while (t is not null)
                {
                    if (t.FullName?.Contains("Xunit") == true) return true;
                    t = t.BaseType;
                }
                return false;
            }

            var isAssertion = IsXunitException(inner) ||
                              inner.Message.Contains("Assert.") ||
                              inner.Message.Contains("Expected:") ||
                              inner.Message.Contains("Actual:") ||
                              inner.Message.Contains("Expected status") ||
                              inner.Message.Contains("Expected Bad") ||
                              inner.Message.Contains("Sub-string not found") ||
                              inner.Message.Contains("but got");

            var status = isAssertion ? TestStatus.Failed : TestStatus.Error;
            var icon   = isAssertion ? "❌" : "💥";
            Log($"    {icon} {methodName} — {(isAssertion ? "FAILED" : "ERROR")}: {inner.Message}");

            var actualStatus = ExtractStatusCode(inner.Message);

            return new TestResult
            {
                TestCase         = specificTestCase,
                Status           = status,
                ActualStatusCode = actualStatus,
                ErrorMessage     = inner.Message,
                StackTrace       = inner.StackTrace,
                Duration         = sw.Elapsed
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static int? ExtractStatusCode(string message)
    {
        var match = Regex.Match(message, @"\b([1-5]\d{2})\b");
        return match.Success ? int.Parse(match.Value) : null;
    }

    private static TestResult CreateSkipped(TestCase tc, string reason) => new()
    {
        TestCase = tc,
        Status = TestStatus.Skipped,
        ErrorMessage = reason,
        Duration = TimeSpan.Zero
    };

    private static readonly Lock _consoleLock = new();

    private static void Log(string msg)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[CodeExecutor] {msg}");
            Console.ResetColor();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Roslyn references builder
    // ─────────────────────────────────────────────────────────────────────────

    private static List<MetadataReference> BuildBaseReferences()
    {
        var refs = new List<MetadataReference>();

        // Load ALL trusted platform assemblies — safest approach for Roslyn
        var trustedAssemblies = AppContext
            .GetData("TRUSTED_PLATFORM_ASSEMBLIES")?
            .ToString()?
            .Split(Path.PathSeparator) ?? [];

        foreach (var path in trustedAssemblies)
        {
            if (File.Exists(path))
            {
                try { refs.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* skip unreadable assemblies */ }
            }
        }

        // xUnit assemblies — find via NuGet cache or loaded assemblies
        var xunitNames = new[] { "xunit.core", "xunit.assert", "xunit.abstractions" };

        foreach (var name in xunitNames)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);

            if (asm is not null && !string.IsNullOrEmpty(asm.Location))
            {
                refs.Add(MetadataReference.CreateFromFile(asm.Location));
                continue;
            }

            var nugetPath = FindInNugetCache(name);
            if (nugetPath is not null)
                refs.Add(MetadataReference.CreateFromFile(nugetPath));
        }

        return refs;
    }

    private static string? FindInNugetCache(string assemblyName)
    {
        var nugetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");

        if (!Directory.Exists(nugetDir)) return null;

        var files = Directory.GetFiles(nugetDir, $"{assemblyName}.dll", SearchOption.AllDirectories);
        return files
            .Where(f => f.Contains("net") && !f.Contains("netstandard1"))
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal types
    // ─────────────────────────────────────────────────────────────────────────

    private record CompileResult(
        bool Success,
        Assembly? Assembly,
        IReadOnlyList<string> Errors);
}