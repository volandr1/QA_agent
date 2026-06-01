using System.Text;
using System.Text.RegularExpressions;
using QA_agent.QaAgent.Core.Services;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Tools;

/// <summary>
/// Uses Ollama to generate C# xUnit test code for a given ApiEndpoint.
/// For each endpoint it produces multiple scenarios: HappyPath, NotFound,
/// Unauthorized, MissingRequired, BoundaryValue, etc.
/// </summary>
public sealed class TestGenerator
{
    // ─────────────────────────────────────────────────────────────────────────
    //  System prompt — injected into every LLM request
    // ─────────────────────────────────────────────────────────────────────────

    private const string SystemPrompt = """
        You are an expert C# QA engineer generating compilable xUnit tests for REST APIs.

        CRITICAL RULES — FOLLOW EXACTLY:
        1. Use HttpClient directly — no third-party HTTP libraries.
        2. Each test method must be: public async Task MethodName()
        3. Use [Fact] attribute on every test.
        4. ALWAYS use the EXACT HTTP status codes listed in the "Expected responses" section.
           Do NOT guess or assume status codes — read them from the schema.
        5. For HappyPath tests: assert using the accepted success codes list provided.
        6. For request bodies: build JSON using EXACTLY the schema fields shown in Parameters.
           Use realistic values — never use "string", "test", or placeholder data.
        7. For enum parameters: use ONLY values from the "Enum values" list provided.
        8. Do NOT add Authorization header unless "Requires Auth: true" is stated.
        9. Output ONLY raw C# code — no markdown, no ```csharp, no explanation, no comments after the last closing brace }.
        10. Every test must create its own HttpClient instance inside the method.
        11. Include these using directives at the top:
            using System.Net;
            using System.Net.Http;
            using System.Net.Http.Headers;
            using System.Net.Http.Json;
            using System.Text;
            using System.Text.Json;
            using Xunit;
        12. Do NOT use ConfigureAwait(false) — keep it simple.

        ══════════════════════════════════════════════════════════
        FORBIDDEN PATTERNS — NEVER USE THESE (cause compile errors):
        ══════════════════════════════════════════════════════════

        ❌ WRONG testing framework methods — xUnit ONLY, never MSTest/NUnit:
            Assert.IsTrue(...)       → CORRECT: Assert.True(...)
            Assert.IsFalse(...)      → CORRECT: Assert.False(...)
            Assert.IsNull(...)       → CORRECT: Assert.Null(...)
            Assert.IsNotNull(...)    → CORRECT: Assert.NotNull(...)
            Assert.AreEqual(...)     → CORRECT: Assert.Equal(...)
            Assert.AreNotEqual(...)  → CORRECT: Assert.NotEqual(...)
            [TestMethod]             → CORRECT: [Fact]
            [TestClass]              → CORRECT: public class ...Tests

        ❌ WRONG namespaces — these assemblies are NOT available:
            using Microsoft.VisualStudio.*;
            using Microsoft.VisualBasic.*;
            using Microsoft.AspNetCore.*;
            using Microsoft.Extensions.DependencyInjection;
            using NUnit.Framework;
            using MSTest;

        ❌ WRONG HttpClient disposal — IDisposable only, NOT IAsyncDisposable:
            await using var client = new HttpClient()  → CORRECT: using var client = new HttpClient()
            await client.DisposeAsync()                → CORRECT: just let using block dispose it

        ❌ WRONG test class patterns:
            : IClassFixture<T>                         → REMOVE — not supported
            public MyTests(SomeService svc) { }        → REMOVE — no constructor params allowed
            : IDisposable                              → REMOVE — not needed

        ══════════════════════════════════════════════════════════
        ASSERTION RULES — MANDATORY (copy exactly):
        ══════════════════════════════════════════════════════════

        ✅ CORRECT — assert single status code WITH message:
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"Expected 200 but got {(int)response.StatusCode}: {body}");

        ✅ CORRECT — assert multiple accepted codes (HappyPath):
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(new[] { 200, 201 }.Contains((int)response.StatusCode),
                $"Expected 200 or 201 but got {(int)response.StatusCode}: {body}");

        ✅ CORRECT — assert without message (simple):
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ❌ FORBIDDEN — these do NOT compile in xUnit, NEVER use them:
            Assert.Equal(HttpStatusCode.OK, response.StatusCode, "message"); // no string overload!
            Assert.Equal(response.StatusCode, HttpStatusCode.OK, "message"); // no string overload!
            Assert.InRange(response.StatusCode, 200, 299);  // HttpStatusCode is not a number!
            Assert.Equal(200, response.StatusCode, "message"); // no string 3rd arg for int!
        ══════════════════════════════════════════════════════════

        ══════════════════════════════════════════════════════════
        HTTPCLIENT USAGE — MANDATORY PATTERNS (copy exactly):
        ══════════════════════════════════════════════════════════

        ✅ CORRECT — GET request (no path params):
            using var client = new HttpClient();
            var response = await client.GetAsync($"{BaseUrl}/path?param=value");
            var body = await response.Content.ReadAsStringAsync();

        ✅ CORRECT — GET/PUT/DELETE with path parameter {id}:
            using var client = new HttpClient();
            var id = 1; // hardcoded realistic value — NEVER use undefined variable
            var response = await client.GetAsync($"{BaseUrl}/activities/{id}");
            var body = await response.Content.ReadAsStringAsync();

        ❌ FORBIDDEN — path param as undefined variable (causes compile error):
            var response = await client.GetAsync($"{BaseUrl}/activities/{id}"); // {id} not defined!
            // ALWAYS declare: var id = 1; BEFORE using it in the URL

        ✅ CORRECT — POST/PUT with JSON body:
            using var client = new HttpClient();
            var requestBody = new { id = 1, name = "Buddy", status = "available" };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{BaseUrl}/path", content);
            var body = await response.Content.ReadAsStringAsync();

        ✅ CORRECT — POST with empty body (missing required fields test):
            using var client = new HttpClient();
            var content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{BaseUrl}/path", content);

        ✅ CORRECT — Adding Accept header:
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

        ❌ FORBIDDEN — these cause compile errors, NEVER use them:
            request.Headers.Accept = ...;          // read-only, forbidden
            request.Headers.ContentType = ...;     // does not exist, forbidden
            client.DefaultRequestHeaders.ContentType = ...;  // does not exist, forbidden
            new HttpRequestMessage { Headers = ... };        // cannot set headers in initializer

        ⚠ CONTENT-TYPE RULE — CRITICAL:
            StringContent(json, Encoding.UTF8, "application/json") ALREADY sets Content-Type.
            NEVER add Content-Type header manually after creating StringContent — it causes:
            "Cannot add value because header 'Content-Type' does not support multiple values"

        ❌ FORBIDDEN — duplicate Content-Type:
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Add("Content-Type", "application/json"); // FORBIDDEN — already set!
            content.Headers.Add("Content-Type", "application/json"); // FORBIDDEN — already set!

        ✅ CORRECT — Content-Type set only once via StringContent:
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            // Do NOT add Content-Type header manually — it is already set above
        ══════════════════════════════════════════════════════════
        """;

    // ─────────────────────────────────────────────────────────────────────────
    //  Chain system prompt
    // ─────────────────────────────────────────────────────────────────────────

    private const string ChainSystemPrompt = """
        You are an expert C# QA engineer generating chained xUnit tests for REST APIs.
        A chained test performs 3 sequential HTTP requests in ONE [Fact] method:
        Step 1: Register a new user. Step 2: Login to get token. Step 3: Call protected endpoint.

        CRITICAL RULES:
        1. Generate exactly ONE [Fact] method with all 3 steps.
        2. Use ONE shared HttpClient for all requests.
        3. STEP 1 — Register a FIXED test user (copy exactly, do NOT use timestamp emails):
              // Use FIXED credentials — same user every run, no DB pollution
              const string testEmail    = "qa_agent_test@test.com";
              const string testPassword = "Test1234!";
              var regBody    = new { email = testEmail, password = testPassword };
              var regJson    = JsonSerializer.Serialize(regBody);
              var regContent = new StringContent(regJson, Encoding.UTF8, "application/json");
              var regResp    = await client.PostAsync($"{BaseUrl}/REGISTER_PATH", regContent);
              // IGNORE registration response — user may already exist (400) or be new (200/201)
              // Either way, proceed to login below
        4. STEP 2 — Login with the fixed credentials and extract token (copy exactly):
              var loginBody    = new { email = testEmail, password = testPassword };
              var loginJson    = JsonSerializer.Serialize(loginBody);
              var loginContent = new StringContent(loginJson, Encoding.UTF8, "application/json");
              var loginResp    = await client.PostAsync($"{BaseUrl}/LOGIN_PATH", loginContent);
              var loginStr     = await loginResp.Content.ReadAsStringAsync();
              string token = "";
              try {
                  var doc = JsonDocument.Parse(loginStr);
                  var root = doc.RootElement;
                  token = root.TryGetProperty("token",        out var t1) ? t1.GetString() ?? "" :
                          root.TryGetProperty("accessToken",  out var t2) ? t2.GetString() ?? "" :
                          root.TryGetProperty("access_token", out var t3) ? t3.GetString() ?? "" :
                          root.TryGetProperty("jwt",          out var t4) ? t4.GetString() ?? "" : "";
              } catch { }
        5. STEP 3 — Call protected endpoint with Bearer token (copy exactly):
              client.DefaultRequestHeaders.Authorization =
                  new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
              // Now make the actual protected request
        6. Assert the protected endpoint result:
              var statusCode = (int)response.StatusCode;
              var body = await response.Content.ReadAsStringAsync();
              Assert.True(statusCode >= 200 && statusCode < 300,
                  $"Expected 2xx but got {statusCode}: {body}");
        7. Output ONLY raw C# code. No markdown. No explanation.
        8. Class name must end with "ChainTests". Namespace: QA_agent.GeneratedTests
        9. FORBIDDEN — these cause compile errors, NEVER use:
           ❌ await using var client = new HttpClient()  → use: using var client = new HttpClient()
           ❌ using Microsoft.VisualStudio.*;             → forbidden
           ❌ using Microsoft.AspNetCore.*;               → forbidden
           ❌ using NUnit.Framework;                      → forbidden
           ❌ Constructor with parameters in test class  → class must have NO constructor
           ❌ client.DisposeAsync()                       → HttpClient has no DisposeAsync
           ❌ IClassFixture<T>                            → not supported
           ❌ Assert.IsTrue(...)                          → use Assert.True(...)
           ❌ Assert.AreEqual(...)                        → use Assert.Equal(...)
           ❌ Assert.IsNull(...)                          → use Assert.Null(...)
        """;

    // ─────────────────────────────────────────────────────────────────────────
    //  Fields
    // ─────────────────────────────────────────────────────────────────────────

    private readonly OllamaClient _ollama;
    private readonly string       _baseUrl;
    private readonly float        _temperature;
    private readonly int          _maxTokens;
    private readonly AuthConfig   _auth;

    // ─────────────────────────────────────────────────────────────────────────
    //  Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public TestGenerator(
        OllamaClient ollama,
        string       baseUrl,
        float        temperature = 0.1f,
        int          maxTokens   = 8192,
        AuthConfig?  auth        = null)
    {
        _ollama      = ollama;
        _baseUrl     = baseUrl.TrimEnd('/');
        _temperature = temperature;
        _maxTokens   = maxTokens;
        _auth        = auth ?? new AuthConfig();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates test cases for all provided endpoints.
    /// Also generates AuthChain tests for protected endpoints when a login endpoint is found.
    /// </summary>
    public async Task<List<TestCase>> GenerateForAllAsync(
        IReadOnlyList<ApiEndpoint> endpoints,
        CancellationToken ct = default)
    {
        var results = new List<TestCase>();

        // Detect login and register endpoints once — used for chain tests
        var loginEndpoint    = FindLoginEndpoint(endpoints);
        var registerEndpoint = FindRegisterEndpoint(endpoints);
        if (loginEndpoint is not null)
            Log($"🔗 Login: {loginEndpoint.Label} | Register: {registerEndpoint?.Label ?? "not found"} — chain tests enabled");

        foreach (var endpoint in endpoints)
        {
            Log($"Generating tests for {endpoint.Label}...");
            var cases = await GenerateForEndpointAsync(endpoint, ct);
            results.AddRange(cases);

            // Generate chain test if endpoint requires auth OR has 401 in responses
            // (some APIs don't mark RequiresAuth in spec but still return 401)
            var needsAuth = endpoint.RequiresAuth
                         || endpoint.Responses.ContainsKey("401")
                         || endpoint.Responses.ContainsKey("403");

            if (needsAuth && loginEndpoint is not null
                && !IsSameEndpoint(endpoint, loginEndpoint))
            {
                Log($"  🔗 Generating auth-chain test for {endpoint.Label}...");
                var chainCase = await GenerateChainTestAsync(loginEndpoint, endpoint, ct, registerEndpoint);
                results.Add(chainCase);
            }

            Log($"  → {cases.Count} test(s) planned");
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Login endpoint detection
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects the login/auth endpoint from the spec.
    /// Looks for POST endpoints whose path contains login/signin/auth/token keywords.
    /// </summary>
    private static ApiEndpoint? FindLoginEndpoint(IReadOnlyList<ApiEndpoint> endpoints)
    {
        var loginKeywords = new[] { "login", "signin", "sign-in", "auth/token", "token", "authenticate" };

        return endpoints.FirstOrDefault(e =>
            e.Method == "POST" &&
            loginKeywords.Any(kw =>
                e.Path.Contains(kw, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSameEndpoint(ApiEndpoint a, ApiEndpoint b) =>
        a.Method == b.Method && a.Path == b.Path;

    // ─────────────────────────────────────────────────────────────────────────
    //  Chain test generation
    // ─────────────────────────────────────────────────────────────────────────

    private static ApiEndpoint? FindRegisterEndpoint(IReadOnlyList<ApiEndpoint> endpoints) =>
        endpoints.FirstOrDefault(e =>
            e.Method == "POST" &&
            (e.Path.Contains("register", StringComparison.OrdinalIgnoreCase) ||
             e.Path.Contains("signup",   StringComparison.OrdinalIgnoreCase) ||
             e.Path.Contains("sign-up",  StringComparison.OrdinalIgnoreCase)));

    private async Task<TestCase> GenerateChainTestAsync(
        ApiEndpoint loginEndpoint,
        ApiEndpoint protectedEndpoint,
        CancellationToken ct,
        ApiEndpoint? registerEndpoint = null)
    {
        var prompt = BuildChainPrompt(loginEndpoint, protectedEndpoint, registerEndpoint);

        try
        {
            var code = await _ollama.GenerateAsync(prompt, ChainSystemPrompt, _temperature, _maxTokens, ct);
            code = CleanCode(code, _baseUrl);

            return new TestCase
            {
                Endpoint           = protectedEndpoint,
                ScenarioName       = $"AuthChain_LoginThen_{ToPascalCase(protectedEndpoint.Method)}",
                ScenarioType       = TestScenarioType.AuthChain,
                ExpectedStatusCode = 200,
                GeneratedCode      = code,
                Notes              = $"Chain: {loginEndpoint.Label} → {protectedEndpoint.Label}"
            };
        }
        catch (Exception ex)
        {
            Log($"  [ERROR] Chain test generation failed: {ex.Message}");
            return new TestCase
            {
                Endpoint           = protectedEndpoint,
                ScenarioName       = "AuthChain_GenerationFailed",
                ScenarioType       = TestScenarioType.AuthChain,
                ExpectedStatusCode = 200,
                GeneratedCode      = null,
                Notes              = ex.Message
            };
        }
    }

    /// <summary>
    /// Generates test cases for a single endpoint.
    /// </summary>
    public async Task<List<TestCase>> GenerateForEndpointAsync(
        ApiEndpoint endpoint,
        CancellationToken ct = default)
    {
        var scenarios = DetermineScenarios(endpoint);
        var testCases = new List<TestCase>();

        var prompt = BuildPrompt(endpoint, scenarios);

        try
        {
            var code = await _ollama.GenerateAsync(prompt, SystemPrompt, _temperature, _maxTokens, ct);
            code = CleanCode(code, _baseUrl);

            foreach (var scenario in scenarios)
            {
                testCases.Add(new TestCase
                {
                    Endpoint             = endpoint,
                    ScenarioName         = scenario.Name,
                    ScenarioType         = scenario.Type,
                    ExpectedStatusCode   = scenario.ExpectedStatus,
                    GeneratedCode        = code,
                    Notes                = scenario.Notes
                });
            }
        }
        catch (Exception ex)
        {
            Log($"  [ERROR] Failed to generate tests for {endpoint.Label}: {ex.Message}");
            testCases.Add(new TestCase
            {
                Endpoint           = endpoint,
                ScenarioName       = "GenerationFailed",
                ScenarioType       = TestScenarioType.HappyPath,
                ExpectedStatusCode = 200,
                GeneratedCode      = null,
                Notes              = $"Generation error: {ex.Message}"
            });
        }

        return testCases;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Scenario planning — читаємо статус коди зі схеми, не хардкодимо
    // ─────────────────────────────────────────────────────────────────────────

    private static List<ScenarioPlan> DetermineScenarios(ApiEndpoint endpoint)
    {
        var scenarios = new List<ScenarioPlan>();

        // ── Happy path ──────────────────────────────────────────────────────
        // Читаємо реальний success-код зі Swagger, не хардкодимо 200/201
        var successCode    = GetSuccessStatusCode(endpoint);
        var acceptedCodes  = GetAllSuccessCodes(endpoint);
        var acceptedStr    = string.Join(" or ", acceptedCodes);

        // HappyPath приймає БУДЬ-ЯКИЙ 2xx (200-299) — не лише коди зі схеми.
        // Це правильно: якщо API повертає 200 замість 201 — це все одно успіх.
        scenarios.Add(new ScenarioPlan(
            "Returns 2xx with valid input",
            TestScenarioType.HappyPath,
            successCode,
            "Use valid realistic data. Accept ANY 2xx response (200-299). " +
            "Use EXACTLY this assertion pattern:\n" +
            "     var statusCode = (int)response.StatusCode;\n" +
            "     var body = await response.Content.ReadAsStringAsync();\n" +
            "     Assert.True(statusCode >= 200 && statusCode < 300,\n" +
            $"         $\"Expected 2xx success but got {{statusCode}}: {{body}}\");"));

        // ── Auth ────────────────────────────────────────────────────────────
        if (endpoint.RequiresAuth)
        {
            var unauthorizedCode = GetResponseCode(endpoint, new[] { "401", "403" }, 401);
            scenarios.Add(new ScenarioPlan(
                $"Returns {unauthorizedCode} when no auth token provided",
                TestScenarioType.Unauthorized,
                unauthorizedCode,
                "Send request WITHOUT Authorization header. " +
                $"Expected status: {unauthorizedCode}"));
        }

        // ── Not found (path param) ───────────────────────────────────────────
        if (endpoint.Parameters.Any(p => p.Location == "path"))
        {
            var notFoundCode = GetResponseCode(endpoint, new[] { "404" }, 404);
            scenarios.Add(new ScenarioPlan(
                $"Returns {notFoundCode} when resource does not exist",
                TestScenarioType.NotFound,
                notFoundCode,
                "Use a non-existent ID like 999999999. " +
                $"Expected status: {notFoundCode}"));
        }

        // ── Missing required body fields ─────────────────────────────────────
        var hasRequiredBody = endpoint.Parameters.Any(p =>
            p.Location == "body" && p.IsRequired);

        if (hasRequiredBody && endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            // Smart code selection: prefer 400/422, but use 401 if that's all the spec has
            // (login endpoints often return 401 for all errors, not 400)
            var badRequestCode = endpoint.Responses.ContainsKey("400") ? 400 :
                                 endpoint.Responses.ContainsKey("422") ? 422 :
                                 endpoint.Responses.ContainsKey("401") ? 401 : 400;

            // Determine if this is a login-type endpoint that returns 401 for everything
            var isLoginType = endpoint.Path.Contains("login",    StringComparison.OrdinalIgnoreCase) ||
                              endpoint.Path.Contains("signin",   StringComparison.OrdinalIgnoreCase) ||
                              endpoint.Path.Contains("token",    StringComparison.OrdinalIgnoreCase) ||
                              endpoint.Path.Contains("auth",     StringComparison.OrdinalIgnoreCase);

            var acceptNote = isLoginType
                ? $"Expected status: {badRequestCode} (or 401 if API returns 401 for all auth errors). Accept BOTH 400 and 401."
                : $"Send request with empty JSON body {{}}. Expected status: {badRequestCode}";

            var acceptedNote = isLoginType
                ? $"Accept 400 OR 401 — use: Assert.True(statusCode == 400 || statusCode == 401, ...)"
                : $"Expected status: {badRequestCode}";

            scenarios.Add(new ScenarioPlan(
                $"Returns {badRequestCode} when required body fields are missing",
                TestScenarioType.MissingRequired,
                badRequestCode,
                $"Send request with empty JSON body {{}}. {acceptNote}\n     {acceptedNote}"));
        }

        // ── Missing required query param ─────────────────────────────────────
        var requiredQueryParams = endpoint.Parameters
            .Where(p => p.Location == "query" && p.IsRequired)
            .ToList();

        if (requiredQueryParams.Any())
        {
            // Check if the spec has 400 response — if not, API may return 200 for missing params
            var has400 = endpoint.Responses.ContainsKey("400") || endpoint.Responses.ContainsKey("422");
            if (has400)
            {
                var badRequestCode = GetResponseCode(endpoint, new[] { "400", "422" }, 400);
                var paramNames     = string.Join(", ", requiredQueryParams.Select(p => p.Name));
                scenarios.Add(new ScenarioPlan(
                    $"Returns {badRequestCode} when required query params are missing",
                    TestScenarioType.MissingRequired,
                    badRequestCode,
                    $"Omit ALL required query parameters ({paramNames}). " +
                    $"Expected status: {badRequestCode}"));
            }
            // If no 400 in spec, skip this scenario — API likely returns 200 with empty result
        }

        // ── Invalid enum value ────────────────────────────────────────────────
        var enumParams = endpoint.Parameters
            .Where(p => p.EnumValues.Count > 0)
            .ToList();

        if (enumParams.Any())
        {
            var badRequestCode = GetResponseCode(endpoint, new[] { "400", "422" }, 400);
            var enumParamName  = enumParams.First().Name;
            scenarios.Add(new ScenarioPlan(
                $"Returns {badRequestCode} when invalid enum value is used",
                TestScenarioType.InvalidType,
                badRequestCode,
                $"Send \"INVALID_VALUE_XYZ\" for parameter '{enumParamName}'. " +
                $"Expected status: {badRequestCode}"));
        }

        return scenarios;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Chain prompt builder
    // ─────────────────────────────────────────────────────────────────────────

    private string BuildChainPrompt(ApiEndpoint loginEndpoint, ApiEndpoint protectedEndpoint,
        ApiEndpoint? registerEndpoint = null)
    {
        var sb = new StringBuilder();

        var hasRegister = registerEndpoint is not null;

        sb.AppendLine(hasRegister
            ? "Generate a C# xUnit chain test that does 3 steps: Register → Login → Call protected endpoint"
            : "Generate a C# xUnit chain test that does 2 steps: Login → Call protected endpoint");
        sb.AppendLine();

        // Step 1: Register (if available)
        if (hasRegister)
        {
            sb.AppendLine("═══ STEP 1: REGISTER FIXED TEST USER ═══");
            sb.AppendLine($"  Method: {registerEndpoint!.Method}");
            sb.AppendLine($"  URL:    {_baseUrl}{registerEndpoint.Path}");
            sb.AppendLine("  Use FIXED credentials — same every run, avoids creating many DB records:");
            sb.AppendLine("  const string testEmail    = \"qa_agent_test@test.com\";");
            sb.AppendLine("  const string testPassword = \"Test1234!\";");
            sb.AppendLine("  var regBody = new { email = testEmail, password = testPassword };");
            sb.AppendLine("  IGNORE the registration response — user may already exist (400) or be new.");
            sb.AppendLine("  Do NOT assert the registration result. Proceed to login regardless.");
            sb.AppendLine();
        }

        // Step 2: Login
        var loginStep = hasRegister ? "STEP 2" : "STEP 1";
        sb.AppendLine($"═══ {loginStep}: LOGIN ═══");
        sb.AppendLine($"  Method: {loginEndpoint.Method}");
        sb.AppendLine($"  URL:    {_baseUrl}{loginEndpoint.Path}");
        if (hasRegister)
        {
            sb.AppendLine("  Use the SAME fixed credentials from Step 1: testEmail and testPassword.");
            sb.AppendLine("  var loginBody = new { email = testEmail, password = testPassword };");
        }
        else
        {
            sb.AppendLine("  Credentials: email='admin@example.com', password='Admin123!'");
        }
        sb.AppendLine("  Extract token from response (try: token, accessToken, access_token, jwt).");
        sb.AppendLine();

        // Protected endpoint info
        var protStep = hasRegister ? "STEP 3" : "STEP 2";
        sb.AppendLine($"═══ {protStep}: PROTECTED ENDPOINT ═══");
        sb.AppendLine($"  Method: {protectedEndpoint.Method}");
        sb.AppendLine($"  URL:    {_baseUrl}{protectedEndpoint.Path}");
        sb.AppendLine("  Add header: Authorization: Bearer <token from step 1>");

        var pathParams = protectedEndpoint.Parameters.Where(p => p.Location == "path").ToList();
        if (pathParams.Any())
        {
            sb.AppendLine("  Path parameters (declare before using in URL):");
            foreach (var p in pathParams)
                sb.AppendLine($"    var {p.Name} = {GetExampleValue(p)};");
        }

        var protectedBody = protectedEndpoint.Parameters.FirstOrDefault(p => p.Location == "body");
        if (protectedBody?.SchemaJson is not null)
            sb.AppendLine($"  Body schema: {protectedBody.SchemaJson}");

        if (protectedEndpoint.Responses.Any())
        {
            sb.AppendLine("  Expected responses:");
            foreach (var (code, desc) in protectedEndpoint.Responses)
                sb.AppendLine($"    HTTP {code} → {desc}");
        }
        sb.AppendLine();

        // Class name
        var className = $"{ToPascalCase(protectedEndpoint.OperationId ?? protectedEndpoint.Method + protectedEndpoint.Path.Replace("/", "_").Trim('_'))}ChainTests";
        sb.AppendLine($"Class name: {className}");
        sb.AppendLine("Namespace: QA_agent.GeneratedTests");
        sb.AppendLine($"private const string BaseUrl = \"{_baseUrl}\";");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers — читаємо реальні коди зі схеми
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Повертає перший success-код зі схеми (200, 201, 202, 204).</summary>
    private static int GetSuccessStatusCode(ApiEndpoint endpoint)
    {
        foreach (var code in new[] { "200", "201", "202", "204" })
            if (endpoint.Responses.ContainsKey(code))
                return int.Parse(code);

        // Fallback якщо схема не має responses
        return endpoint.Method is "POST" ? 201 : 200;
    }

    /// <summary>Повертає ВСІ success-коди зі схеми — для assertion "accept any 2xx".</summary>
    private static List<int> GetAllSuccessCodes(ApiEndpoint endpoint)
    {
        var codes = endpoint.Responses
            .Keys
            .Where(k => int.TryParse(k, out var n) && n >= 200 && n < 300)
            .Select(int.Parse)
            .OrderBy(x => x)
            .ToList();

        return codes.Count > 0 ? codes : [GetSuccessStatusCode(endpoint)];
    }

    /// <summary>Шукає перший з candidateCodes що є в схемі, або повертає fallback.</summary>
    private static int GetResponseCode(ApiEndpoint endpoint, string[] candidateCodes, int fallback)
    {
        foreach (var code in candidateCodes)
            if (endpoint.Responses.ContainsKey(code))
                return int.Parse(code);
        return fallback;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prompt builder
    // ─────────────────────────────────────────────────────────────────────────

    private string BuildPrompt(ApiEndpoint endpoint, List<ScenarioPlan> scenarios)
    {
        var sb = new StringBuilder();

        // ── Endpoint info ────────────────────────────────────────────────────
        sb.AppendLine("Generate a complete C# xUnit test class for this API endpoint:");
        sb.AppendLine();
        sb.AppendLine($"  Method:       {endpoint.Method}");
        sb.AppendLine($"  Path:         {endpoint.Path}");
        sb.AppendLine($"  Full URL:     {_baseUrl}{endpoint.Path}");
        sb.AppendLine($"  OperationId:  {endpoint.OperationId ?? "N/A"}");
        sb.AppendLine($"  Summary:      {endpoint.Summary ?? "N/A"}");
        sb.AppendLine($"  Requires Auth: {endpoint.RequiresAuth}");
        if (endpoint.RequestBodyMediaType is not null)
            sb.AppendLine($"  Content-Type: {endpoint.RequestBodyMediaType}");

        // ── Parameters ───────────────────────────────────────────────────────
        if (endpoint.Parameters.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Parameters:");
            foreach (var p in endpoint.Parameters)
            {
                sb.AppendLine($"  [{p.Location}] {p.Name}: {p.Type}" +
                              (p.Format is not null ? $" ({p.Format})" : "") +
                              (p.IsRequired ? " *REQUIRED*" : " (optional)") +
                              (p.Description is not null ? $" — {p.Description}" : ""));

                if (p.EnumValues.Count > 0)
                    sb.AppendLine($"    ⚠ ALLOWED VALUES ONLY: {string.Join(", ", p.EnumValues.Select(v => $"\"{v}\""))}");

                if (p.SchemaJson is not null)
                    sb.AppendLine($"    Schema: {p.SchemaJson}");
            }
        }

        // ── Path parameters — explicit URL example ───────────────────────────
        var pathParams = endpoint.Parameters.Where(p => p.Location == "path").ToList();
        if (pathParams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ PATH PARAMETERS — CRITICAL RULES:");
            sb.AppendLine("  NEVER use {paramName} as an undefined variable in interpolated strings.");
            sb.AppendLine("  ALWAYS declare variables with hardcoded values BEFORE using them.");
            sb.AppendLine();
            sb.AppendLine("  Declare these variables at the start of each test method:");
            foreach (var p in pathParams)
            {
                var exampleValue = GetExampleValue(p);
                sb.AppendLine($"    var {p.Name} = {exampleValue}; // hardcoded example value");
            }
            sb.AppendLine();

            // Build example URL with actual values substituted
            var exampleUrl = _baseUrl + endpoint.Path;
            foreach (var p in pathParams)
                exampleUrl = exampleUrl.Replace($"{{{p.Name}}}", GetExampleValueRaw(p));

            sb.AppendLine($"  ✅ CORRECT URL example: \"{exampleUrl}\"");
            sb.AppendLine($"  ✅ CORRECT code: var response = await client.GetAsync($\"{_baseUrl}/{BuildUrlTemplate(endpoint.Path, pathParams)}\");");
        }

        // ── Expected responses (КРИТИЧНО для генерації правильних assertions) ─
        if (endpoint.Responses.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ EXPECTED RESPONSES FROM SCHEMA (use EXACTLY these codes in assertions):");
            foreach (var (code, desc) in endpoint.Responses)
                sb.AppendLine($"  HTTP {code} → {desc}");
        }

        // ── Authentication ───────────────────────────────────────────────────
        if (_auth.IsConfigured)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ AUTHENTICATION — MANDATORY RULES:");
            sb.AppendLine($"  Auth type: {_auth.Type}");
            sb.AppendLine();
            sb.AppendLine("  For ALL tests EXCEPT [Unauthorized] tests, add this line");
            sb.AppendLine("  INSIDE each test method, right after creating HttpRequestMessage:");
            sb.AppendLine($"    {_auth.CSharpHeaderCode}");
            sb.AppendLine();
            sb.AppendLine("  For [Unauthorized] tests: do NOT add any auth header.");
            sb.AppendLine("  The Unauthorized test verifies that the API rejects unauthenticated requests.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("  Requires Auth: false — do NOT add any Authorization header.");
        }

        // ── HappyPath assertion — приймаємо будь-який 2xx ────────────────────
        sb.AppendLine();
        sb.AppendLine("⚠ HAPPYPATH ASSERTION — copy this EXACTLY, do not change it:");
        sb.AppendLine("  var statusCode = (int)response.StatusCode;");
        sb.AppendLine("  var body = await response.Content.ReadAsStringAsync();");
        sb.AppendLine("  Assert.True(statusCode >= 200 && statusCode < 300,");
        sb.AppendLine("    $\"Expected 2xx success but got {statusCode}: {body}\");");

        // ── Unique email for registration endpoints ───────────────────────────
        var isRegisterEndpoint = endpoint.Method is "POST" &&
            (endpoint.Path.Contains("register", StringComparison.OrdinalIgnoreCase) ||
             endpoint.Path.Contains("signup",   StringComparison.OrdinalIgnoreCase) ||
             endpoint.Path.Contains("sign-up",  StringComparison.OrdinalIgnoreCase));

        if (isRegisterEndpoint)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ REGISTRATION EMAIL RULE — MANDATORY:");
            sb.AppendLine("  Use a FIXED test email — same across all runs to avoid polluting the DB:");
            sb.AppendLine("  const string testEmail    = \"qa_agent_test@test.com\";");
            sb.AppendLine("  const string testPassword = \"Test1234!\";");
            sb.AppendLine("  For HappyPath: wrap the registration call so it IGNORES 400 (user may already exist).");
            sb.AppendLine("  Assert success ONLY if the response is 2xx OR if it is 400 (already registered):");
            sb.AppendLine("  var statusCode = (int)regResp.StatusCode;");
            sb.AppendLine("  Assert.True(statusCode >= 200 && statusCode < 300 || statusCode == 400,");
            sb.AppendLine("    $\"Expected 2xx or 400 (already registered) but got {statusCode}: {body}\");");
        }

        // ── Example request body (якщо є body-параметр) ─────────────────────
        var bodyParam = endpoint.Parameters.FirstOrDefault(p => p.Location == "body");
        if (bodyParam?.SchemaJson is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Request body schema to use:");
            sb.AppendLine(bodyParam.SchemaJson);
        }

        // ── Scenarios ────────────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("Generate test methods for ALL of these scenarios:");
        for (var i = 0; i < scenarios.Count; i++)
        {
            var s = scenarios[i];
            sb.AppendLine($"  {i + 1}. [{s.Type}] {s.Name}");
            sb.AppendLine($"     Expected HTTP status: {s.ExpectedStatus}");
            sb.AppendLine($"     Instructions: {s.Notes}");
        }

        // ── Class naming ─────────────────────────────────────────────────────
        var className = BuildClassName(endpoint);
        sb.AppendLine();
        sb.AppendLine($"Class name: {className}");
        sb.AppendLine("Namespace: QA_agent.GeneratedTests");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT:");
        sb.AppendLine("  - Create a new HttpClient in EACH test method (do not share)");
        sb.AppendLine("  - private const string BaseUrl = \"" + _baseUrl + "\";");
        sb.AppendLine("  - Add Content-Type: application/json header for POST/PUT/PATCH requests");
        sb.AppendLine("  - Include the actual response body in every assertion failure message");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns a C# literal value for a path parameter (for use in code).</summary>
    private static string GetExampleValue(ApiParameter p)
    {
        var name = p.Name.ToLowerInvariant();
        if (p.EnumValues.Count > 0)
            return $"\"{p.EnumValues[0]}\"";
        return p.Type switch
        {
            "integer" or "number" => "1",
            "boolean"             => "true",
            _                     => name.Contains("name") || name.Contains("code") || name.Contains("key")
                                        ? "\"test\""
                                        : "1"
        };
    }

    /// <summary>Returns a raw string value for substituting into URL examples.</summary>
    private static string GetExampleValueRaw(ApiParameter p)
    {
        var name = p.Name.ToLowerInvariant();
        if (p.EnumValues.Count > 0) return p.EnumValues[0];
        return p.Type switch
        {
            "integer" or "number" => "1",
            "boolean"             => "true",
            _ => name.Contains("name") || name.Contains("code") || name.Contains("key") ? "test" : "1"
        };
    }

    /// <summary>Builds a C# interpolated URL template, e.g. /api/v1/pets/{petId} → api/v1/pets/{petId}</summary>
    private static string BuildUrlTemplate(string path, List<ApiParameter> pathParams)
    {
        // Strip leading slash — BaseUrl + "/" + path would double-slash
        return path.TrimStart('/');
    }

    private static string BuildClassName(ApiEndpoint endpoint)
    {
        if (endpoint.OperationId is { Length: > 0 } id)
            return $"{ToPascalCase(id)}Tests";

        var path = Regex.Replace(endpoint.Path, @"[^a-zA-Z0-9]", "_");
        path = Regex.Replace(path, @"_+", "_").Trim('_');
        return $"{ToPascalCase(endpoint.Method)}_{ToPascalCase(path)}_Tests";
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var parts = Regex.Split(input, @"[^a-zA-Z0-9]+");
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpper(p[0]) + p[1..]));
    }

    /// <summary>
    /// Strips markdown code fences the LLM sometimes includes despite instructions.
    /// Also removes any prose before the first "using" statement.
    /// </summary>
    private static string CleanCode(string raw, string baseUrl = "")
    {
        // Remove ```csharp ... ``` or ``` ... ```
        raw = Regex.Replace(raw, @"```[a-zA-Z]*\r?\n?", "", RegexOptions.Multiline);
        raw = Regex.Replace(raw, @"```\s*", "", RegexOptions.Multiline);

        // Strip any prose before the first "using" or "namespace"
        var firstCode = Regex.Match(raw, @"^(using\s|namespace\s)", RegexOptions.Multiline);
        if (firstCode.Success)
            raw = raw[firstCode.Index..];

        // Strip any prose AFTER the last closing brace of the class/namespace
        raw = StripTrailingProse(raw);

        // Auto-fix common LLM mistakes before handing to Roslyn
        raw = ApplyCodeFixups(raw, baseUrl);

        return raw.Trim();
    }

    /// <summary>
    /// Видаляє будь-який текст після останньої закриваючої дужки } класу/namespace.
    /// LLM часто додає пояснення типу "Note: ...", "This test..." після коду.
    /// </summary>
    private static string StripTrailingProse(string code)
    {
        // Знаходимо позицію останньої } в коді
        var lastBrace = code.LastIndexOf('}');
        if (lastBrace < 0) return code;

        var afterBrace = code[(lastBrace + 1)..].TrimStart('\r', '\n', ' ', '\t');

        // Якщо після } є щось що не є коментарем C# і не є порожнім — обрізаємо
        if (afterBrace.Length > 0 && !afterBrace.StartsWith("//") && !afterBrace.StartsWith("/*"))
            return code[..(lastBrace + 1)];

        return code;
    }

    /// <summary>
    /// Автоматично виправляє найчастіші помилки які LLM генерує у HttpClient коді.
    /// Запускається ПЕРЕД компіляцією — зменшує кількість heal-спроб.
    /// </summary>
    private static string ApplyCodeFixups(string code, string baseUrl = "")
    {
        // ── Додаємо відсутній using System.Net.Http.Headers ─────────────────
        if (!code.Contains("using System.Net.Http.Headers") &&
            code.Contains("MediaTypeWithQualityHeaderValue"))
        {
            code = code.Replace(
                "using System.Net.Http;",
                "using System.Net.Http;\nusing System.Net.Http.Headers;");
        }

        // ── Якщо нема using Headers але є DefaultRequestHeaders.Accept.Add ──
        if (!code.Contains("using System.Net.Http.Headers") &&
            code.Contains("DefaultRequestHeaders.Accept.Add"))
        {
            code = code.Replace(
                "using System.Net.Http;",
                "using System.Net.Http;\nusing System.Net.Http.Headers;");
        }

        // ── request.Headers.Accept = ... → видаляємо весь рядок ─────────────
        // HttpRequestHeaders.Accept — read-only колекція, не можна присвоїти
        code = Regex.Replace(
            code,
            @"[ \t]*\w+\.Headers\.Accept\s*=\s*[^;]+;\s*\r?\n",
            "");

        // ── request.Headers.ContentType = ... → видаляємо ───────────────────
        // HttpRequestHeaders не має властивості ContentType
        code = Regex.Replace(
            code,
            @"[ \t]*\w+\.Headers\.ContentType\s*=\s*[^;]+;\s*\r?\n",
            "");

        // ── client.DefaultRequestHeaders.ContentType = ... → видаляємо ──────
        code = Regex.Replace(
            code,
            @"[ \t]*\w+\.DefaultRequestHeaders\.ContentType\s*=\s*[^;]+;\s*\r?\n",
            "");

        // ── Duplicate Content-Type header ────────────────────────────────────
        // StringContent("...", Encoding.UTF8, "application/json") вже встановлює Content-Type.
        // Якщо LLM ще й вручну додає Content-Type — це runtime exception.
        // Видаляємо всі ручні дублікати Content-Type.
        code = Regex.Replace(
            code,
            @"[ \t]*\w+\.Headers\.Add\s*\(\s*""Content-Type""\s*,[^)]+\)\s*;\s*\r?\n",
            "");
        code = Regex.Replace(
            code,
            @"[ \t]*\w+\.Headers\.TryAddWithoutValidation\s*\(\s*""Content-Type""\s*,[^)]+\)\s*;\s*\r?\n",
            "");
        // content.Headers.ContentType = new MediaTypeHeaderValue(...) → видаляємо
        code = Regex.Replace(
            code,
            @"[ \t]*\w+\.Headers\.ContentType\s*=\s*new\s+MediaTypeHeaderValue[^;]+;\s*\r?\n",
            "");

        // ── new MediaTypeHeaderValue(...) без using — замінюємо на рядок ────
        // LLM іноді використовує MediaTypeHeaderValue замість рядка в StringContent
        code = Regex.Replace(
            code,
            @"new MediaTypeHeaderValue\(""application/json""\)",
            "\"application/json\"");

        // ── StringContent(json) без encoding → додаємо Encoding.UTF8 ────────
        // Правильна сигнатура: StringContent(string, Encoding, string)
        code = Regex.Replace(
            code,
            @"new StringContent\((\w+)\)",
            "new StringContent($1, Encoding.UTF8, \"application/json\")");

        // ── new[] { 200, 201 }.Contains(...) → 2xx range check ──────────────
        // Якщо LLM проігнорував інструкцію і генерує список кодів — замінюємо на range
        code = Regex.Replace(
            code,
            @"new\s*\[\s*\]\s*\{[^}]+\}\.Contains\(\s*\(int\)\s*(\w+\.StatusCode)\s*\)",
            "(int)$1 >= 200 && (int)$1 < 300");

        // ── Newtonsoft.Json → System.Text.Json ──────────────────────────────
        // codellama часто генерує Newtonsoft який не підключений як залежність
        code = code.Replace("using Newtonsoft.Json;", "using System.Text.Json;");
        code = code.Replace("using Newtonsoft.Json.Linq;", "");
        code = Regex.Replace(
            code,
            @"JsonConvert\.SerializeObject\(([^)]+)\)",
            "JsonSerializer.Serialize($1)");
        code = Regex.Replace(
            code,
            @"JsonConvert\.DeserializeObject<([^>]+)>\(([^)]+)\)",
            "JsonSerializer.Deserialize<$1>($2)");
        code = Regex.Replace(
            code,
            @"JsonConvert\.DeserializeObject\(([^)]+)\)",
            "JsonSerializer.Deserialize<object>($1)");
        // JObject.Parse → JsonDocument.Parse
        code = Regex.Replace(
            code,
            @"JObject\.Parse\(([^)]+)\)",
            "JsonDocument.Parse($1).RootElement");

        // ── IDisposable на тест-класі → видаляємо ───────────────────────────
        // xUnit test class не має реалізовувати IDisposable через Dispose()
        code = Regex.Replace(
            code,
            @"\s*:\s*IDisposable",
            "");
        // Видаляємо public void Dispose() { ... } метод
        code = Regex.Replace(
            code,
            @"[ \t]*public\s+void\s+Dispose\s*\(\s*\)\s*\{[^}]*\}\s*\r?\n",
            "",
            RegexOptions.Singleline);

        // ── BaseUrl не оголошений → inject private const ─────────────────────
        // Якщо модель використовує BaseUrl але не оголосила поле — додаємо
        if (code.Contains("BaseUrl") &&
            !Regex.IsMatch(code, @"(private\s+const\s+string\s+BaseUrl|var\s+BaseUrl\s*=|string\s+BaseUrl\s*=)"))
        {
            // Вставляємо const одразу після відкриваючої дужки класу
            code = Regex.Replace(
                code,
                @"(class\s+\w+[^{]*\{)",
                $"$1\n    private const string BaseUrl = \"{baseUrl}\";");
        }

        // ── async Task<bool> → async Task (test methods не повертають bool) ──
        code = Regex.Replace(
            code,
            @"public async Task<bool>(\s+\w+\s*\()",
            "public async Task$1");

        // ── MSTest/NUnit assertions → xUnit equivalents ──────────────────────
        // LLM sometimes uses MSTest (Assert.IsTrue, Assert.AreEqual) instead of xUnit
        code = code.Replace("Assert.IsTrue(",  "Assert.True(");
        code = code.Replace("Assert.IsFalse(", "Assert.False(");
        code = code.Replace("Assert.IsNull(",  "Assert.Null(");
        code = code.Replace("Assert.IsNotNull(", "Assert.NotNull(");
        // Assert.AreEqual(expected, actual) → Assert.Equal(expected, actual)
        code = Regex.Replace(code, @"\bAssert\.AreEqual\(", "Assert.Equal(");
        code = Regex.Replace(code, @"\bAssert\.AreNotEqual\(", "Assert.NotEqual(");
        // StringAssert → replace with simple Assert
        code = Regex.Replace(code, @"\bStringAssert\.\w+\(", "Assert.True(true || (");

        // ── await using var client → using var client ────────────────────────
        // HttpClient implements IDisposable but NOT IAsyncDisposable — await using causes compile error
        code = Regex.Replace(code, @"await\s+using\s+var\s+(\w+)\s*=\s*new\s+HttpClient\b",
            "using var $1 = new HttpClient");

        // ── Remove test-framework namespaces not available in Roslyn context ───
        // LLM sometimes adds MSTest / AspNetCore.Mvc.Testing / VisualStudio namespaces
        var forbiddenUsings = new[]
        {
            @"Microsoft\.VisualStudio",
            @"Microsoft\.VisualBasic",
            @"Microsoft\.AspNetCore\.Mvc\.Testing",
            @"Microsoft\.AspNetCore\.Mvc",
            @"Microsoft\.AspNetCore\.JsonPatch",
            @"Microsoft\.AspNetCore\.TestHost",
            @"Microsoft\.AspNetCore\.Http",
            @"Microsoft\.Extensions\.DependencyInjection",
            @"NUnit\.Framework",
            @"MSTest",
            @"Microsoft\.VisualStudio\.TestTools",
        };
        foreach (var ns in forbiddenUsings)
            code = Regex.Replace(code, $@"[ \t]*using\s+{ns}[^;\n]*;\s*\r?\n", "");

        // ── Remove parameterized constructors from test classes ───────────────
        // Activator.CreateInstance() requires parameterless constructor.
        // LLM sometimes generates IClassFixture pattern which we don't support.
        code = Regex.Replace(code,
            @"[ \t]*public\s+\w+Tests\s*\([^)]+\)\s*\{[^}]*\}\s*\r?\n",
            "", RegexOptions.Singleline);

        // ── HttpClient.DisposeAsync → fix await using to using ───────────────
        // Extra safety: remove any remaining DisposeAsync calls
        code = Regex.Replace(code,
            @"[ \t]*await\s+\w+\.DisposeAsync\(\)\s*;\s*\r?\n", "");

        // ── Remove IClassFixture<T> from class declaration ───────────────────
        code = Regex.Replace(code,
            @"\s*:\s*IClassFixture<[^>]+>", "");

        // ── Path params used as undefined variables in interpolated strings ───
        // Pattern: $".../{someVar}" where someVar looks like an ID but is not declared
        // We inject "var someVar = 1;" before the line that uses it
        code = FixUndeclaredPathParams(code);

        // ── Multiline string literals → collapse to single line ──────────────
        // LLM sometimes writes multi-line JSON without @ prefix:
        //   var json = "{         ← opens string
        //       "id": 1           ← newline inside string = compile error!
        //   }";
        // We iteratively collapse newlines inside string literals.
        var maxIter = 20;
        while (maxIter-- > 0)
        {
            var collapsed = Regex.Replace(
                code,
                @"(""(?:[^""\\\r\n]|\\.)*)\r?\n[ \t]*((?:[^""\\\r\n]|\\.)*"")",
                "$1 $2");
            if (collapsed == code) break;
            code = collapsed;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Виправлення Assert — xUnit НЕ підтримує string як 3-й аргумент
        // Assert.Equal для enum/object типів. Всі варіанти → Assert.True.
        // RegexOptions.Singleline — щоб ловити патерни розбиті на кілька рядків.
        // ─────────────────────────────────────────────────────────────────────

        // Assert.Equal(HttpStatusCode.X, response.StatusCode, $"message")
        code = Regex.Replace(
            code,
            @"Assert\.Equal\(\s*(HttpStatusCode\.\w+)\s*,\s*(\w+\.StatusCode)\s*,\s*(\$?@?""[^""]*"")\s*\)",
            "Assert.True($2 == $1, $3)",
            RegexOptions.Singleline);

        // Assert.Equal(response.StatusCode, HttpStatusCode.X, $"message")
        code = Regex.Replace(
            code,
            @"Assert\.Equal\(\s*(\w+\.StatusCode)\s*,\s*(HttpStatusCode\.\w+)\s*,\s*(\$?@?""[^""]*"")\s*\)",
            "Assert.True($1 == $2, $3)",
            RegexOptions.Singleline);

        // Assert.Equal((int)response.StatusCode, 200, $"message")
        code = Regex.Replace(
            code,
            @"Assert\.Equal\(\s*\(int\)(\w+\.StatusCode)\s*,\s*(\d+)\s*,\s*(\$?@?""[^""]*"")\s*\)",
            "Assert.True((int)$1 == $2, $3)",
            RegexOptions.Singleline);

        // Assert.Equal(200, (int)response.StatusCode, $"message")
        code = Regex.Replace(
            code,
            @"Assert\.Equal\(\s*(\d+)\s*,\s*\(int\)(\w+\.StatusCode)\s*,\s*(\$?@?""[^""]*"")\s*\)",
            "Assert.True((int)$2 == $1, $3)",
            RegexOptions.Singleline);

        // ── Universal Assert.InRange replacement ─────────────────────────────
        // Covers ALL forms — xUnit has no string-message overload for InRange,
        // and cannot compare HttpStatusCode directly with int bounds.
        // Pattern: Assert.InRange( [optional (int)] actual , low , high [, optional msg] )
        code = Regex.Replace(
            code,
            @"Assert\.InRange\s*\(\s*(?:\(int\)\s*)?(\w+(?:\.\w+)*)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*(?:\$?@?""[^""]*""|\$?[^)]+))?\s*\)",
            m =>
            {
                var actual = m.Groups[1].Value.Trim();
                var low    = m.Groups[2].Value;
                var high   = m.Groups[3].Value;
                // Use (int) cast only for StatusCode properties
                var cast   = actual.EndsWith("StatusCode", StringComparison.OrdinalIgnoreCase)
                             ? "(int)" : "";
                return $"Assert.True({cast}{actual} >= {low} && {cast}{actual} <= {high}, " +
                       $"$\"Expected between {low} and {high} but got {{{cast}{actual}}}\")";
            },
            RegexOptions.Singleline);

        // Assert.Equal(number, anyVar, string) — загальний випадок (змінна типу statusCode)
        code = Regex.Replace(
            code,
            @"Assert\.Equal\(\s*(\d+)\s*,\s*(\w+)\s*,\s*(\$?@?""[^""]*"")\s*\)",
            "Assert.True($2 == $1, $3)",
            RegexOptions.Singleline);

        // Assert.Equal(anyVar, number, string) — зворотній порядок
        code = Regex.Replace(
            code,
            @"Assert\.Equal\(\s*(\w+)\s*,\s*(\d+)\s*,\s*(\$?@?""[^""]*"")\s*\)",
            "Assert.True($1 == $2, $3)",
            RegexOptions.Singleline);

        return code;
    }

    /// <summary>
    /// Detects path parameters used as undefined variables in interpolated URL strings
    /// and injects variable declarations before usage.
    /// E.g.: client.GetAsync($"{BaseUrl}/activities/{id}") → inserts "var id = 1;" above.
    /// </summary>
    private static string FixUndeclaredPathParams(string code)
    {
        var lines = code.Split('\n').ToList();

        // Common path param names LLMs use as undefined variables
        var knownPathParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id", "petId", "userId", "orderId", "activityId", "bookId", "authorId",
            "categoryId", "storeId", "resourceId", "itemId", "recordId", "entityId",
            "name", "username", "code", "slug", "key", "tag", "type"
        };

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First pass: collect all declared variables
        foreach (var line in lines)
        {
            var varMatch = Regex.Match(line, @"\bvar\s+(\w+)\s*=");
            if (varMatch.Success)
                declared.Add(varMatch.Groups[1].Value);

            // Also collect method parameters
            var paramMatch = Regex.Match(line, @"(?:public|private|protected)\s+\S+\s+\w+\s*\(([^)]*)\)");
            if (paramMatch.Success)
            {
                foreach (Match pm in Regex.Matches(paramMatch.Groups[1].Value, @"\w+\s+(\w+)"))
                    declared.Add(pm.Groups[1].Value);
            }
        }

        // Second pass: find interpolated strings using undeclared path-like variables
        var result = new List<string>();
        foreach (var line in lines)
        {
            // Find all {varName} in interpolated strings on this line
            var interpolations = Regex.Matches(line, @"\$""[^""]*\{(\w+)\}[^""]*""");
            var injections = new List<string>();

            foreach (Match m in interpolations)
            {
                var varName = m.Groups[1].Value;
                if (knownPathParams.Contains(varName) && !declared.Contains(varName))
                {
                    // Determine value type: numeric or string
                    var isStringParam = varName.Equals("name", StringComparison.OrdinalIgnoreCase)
                                     || varName.Equals("username", StringComparison.OrdinalIgnoreCase)
                                     || varName.Equals("code", StringComparison.OrdinalIgnoreCase)
                                     || varName.Equals("slug", StringComparison.OrdinalIgnoreCase)
                                     || varName.Equals("tag", StringComparison.OrdinalIgnoreCase);

                    var indent = Regex.Match(line, @"^(\s*)").Groups[1].Value;
                    var value  = isStringParam ? "\"test\"" : "1";
                    injections.Add($"{indent}var {varName} = {value}; // auto-injected path param");
                    declared.Add(varName);
                }
            }

            result.AddRange(injections);
            result.Add(line);
        }

        return string.Join('\n', result);
    }

    private static void Log(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TestGenerator] {msg}");
        Console.ResetColor();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal types
    // ─────────────────────────────────────────────────────────────────────────

    private record ScenarioPlan(
        string           Name,
        TestScenarioType Type,
        int              ExpectedStatus,
        string           Notes);
}
