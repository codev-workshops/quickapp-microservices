using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Identity.Migration;

/// <summary>
/// Saga step 5 (pre-cutover shadow verification). For each seeded account:
///   1. obtain a password-grant token from the monolith and from Identity.API,
///   2. assert both tokens carry the same signing/encryption key id (the shared PFX prerequisite),
///   3. read /api/account/users/me, /api/account/users, /api/account/roles from BOTH apps using
///      the token issued by the OTHER app (cross-validation), and diff the normalized JSON.
/// </summary>
public class ShadowVerify(IConfiguration configuration)
{
    private static readonly string[] ReadPaths = ["api/account/users/me", "api/account/users", "api/account/roles"];
    private static readonly string[] VolatileFields = ["usersCount"]; // may lag between sides for a moment

    public async Task<int> RunAsync()
    {
        var monolith = new Uri(configuration["Verify:MonolithBaseUrl"] ?? "http://localhost:5225");
        var identity = new Uri(configuration["Verify:IdentityBaseUrl"] ?? "http://localhost:5001");
        var clientId = configuration["Verify:ClientId"] ?? "quickapp_spa";

        var accounts = configuration.GetSection("Verify:Accounts").GetChildren()
            .Select(a => (UserName: a["UserName"]!, Password: Environment.GetEnvironmentVariable(a["PasswordEnv"]!)))
            .ToList();

        if (accounts.Count == 0 || accounts.Any(a => string.IsNullOrEmpty(a.Password)))
        {
            Console.Error.WriteLine("Set VERIFY_ADMIN_PASSWORD / VERIFY_USER_PASSWORD (never store them in config).");
            return 1;
        }

        using var http = new HttpClient();
        var failures = 0;

        foreach (var (userName, password) in accounts)
        {
            Console.WriteLine($"== {userName} ==");

            var monolithToken = await GetTokenAsync(http, monolith, clientId, userName, password!);
            var identityToken = await GetTokenAsync(http, identity, clientId, userName, password!);

            if (monolithToken is null || identityToken is null)
            {
                Console.WriteLine($"  FAIL token issuance (monolith={(monolithToken is null ? "failed" : "ok")}, identity={(identityToken is null ? "failed" : "ok")})");
                failures++;
                continue;
            }

            var monolithKid = GetHeader(monolithToken.AccessToken, "kid");
            var identityKid = GetHeader(identityToken.AccessToken, "kid");
            var sameKey = monolithKid is not null && monolithKid == identityKid;
            Console.WriteLine($"  token kid monolith={monolithKid} identity={identityKid} -> {(sameKey ? "SHARED CERT OK" : "FAIL: different signing/encryption key")}");
            if (!sameKey) failures++;

            var sameSubject = monolithToken.Subject == identityToken.Subject;
            Console.WriteLine($"  subject monolith={monolithToken.Subject} identity={identityToken.Subject} -> {(sameSubject ? "OK" : "FAIL")}");
            if (!sameSubject) failures++;

            foreach (var path in ReadPaths)
            {
                // Cross-validate: monolith-issued token against Identity.API and vice-versa.
                var fromMonolith = await ReadAsync(http, monolith, path, identityToken.AccessToken);
                var fromIdentity = await ReadAsync(http, identity, path, monolithToken.AccessToken);

                if (fromMonolith.Status != fromIdentity.Status)
                {
                    Console.WriteLine($"  {path,-28} FAIL status monolith={fromMonolith.Status} identity={fromIdentity.Status}");
                    failures++;
                    continue;
                }

                if (fromMonolith.Status is 401 or 403)
                {
                    Console.WriteLine($"  {path,-28} {fromMonolith.Status} on both sides " +
                                      (fromMonolith.Status == 401 ? "FAIL: token from one side rejected by the other" : "(expected for this account)"));
                    if (fromMonolith.Status == 401) failures++;
                    continue;
                }

                var diff = Diff(Normalize(fromMonolith.Body), Normalize(fromIdentity.Body));
                Console.WriteLine($"  {path,-28} {fromMonolith.Status} {(diff.Count == 0 ? "MATCH" : $"DIFF ({diff.Count})")}");
                foreach (var line in diff.Take(10)) Console.WriteLine($"      {line}");
                if (diff.Count > 0) failures++;
            }
        }

        Console.WriteLine(failures == 0 ? "Shadow verification PASSED." : $"Shadow verification FAILED with {failures} problem(s).");
        return failures == 0 ? 0 : 2;
    }

    private static async Task<TokenResult?> GetTokenAsync(HttpClient http, Uri baseUrl, string clientId, string userName, string password)
    {
        using var response = await http.PostAsync(new Uri(baseUrl, "connect/token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = userName,
            ["password"] = password,
            ["scope"] = "openid email phone profile offline_access roles"
        }));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"  {baseUrl} connect/token -> {(int)response.StatusCode}: {body}");
            return null;
        }

        var json = JsonNode.Parse(body)!;
        var accessToken = json["access_token"]!.GetValue<string>();
        var idToken = json["id_token"]?.GetValue<string>();
        return new TokenResult(accessToken, idToken is null ? null : GetPayloadClaim(idToken, "sub"));
    }

    private static async Task<(int Status, string Body)> ReadAsync(HttpClient http, Uri baseUrl, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string? GetHeader(string jwt, string name) => GetJwtPart(jwt, 0, name);
    private static string? GetPayloadClaim(string jwt, string name) => GetJwtPart(jwt, 1, name);

    private static string? GetJwtPart(string jwt, int index, string name)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var padded = parts[index].Replace('-', '+').Replace('_', '/').PadRight(parts[index].Length + (4 - parts[index].Length % 4) % 4, '=');
        var json = JsonNode.Parse(Convert.FromBase64String(padded));
        return json?[name]?.ToString();
    }

    private static JsonNode? Normalize(string body)
    {
        var node = JsonNode.Parse(body);
        return node is null ? null : Canonicalize(node);
    }

    private static JsonNode Canonicalize(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                var ordered = new JsonObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (VolatileFields.Contains(key)) continue;
                    ordered[key] = value is null ? null : Canonicalize(value);
                }
                return ordered;
            case JsonArray arr:
                var items = arr.Where(i => i is not null).Select(i => Canonicalize(i!)).ToList();
                if (items.All(i => i is JsonObject o && o.ContainsKey("id")))
                    items = items.OrderBy(i => i!["id"]!.ToString(), StringComparer.Ordinal).ToList();
                else if (items.All(i => i is JsonValue))
                    items = items.OrderBy(i => i!.ToJsonString(), StringComparer.Ordinal).ToList();
                return new JsonArray(items.Select(i => i!.DeepClone()).ToArray());
            default:
                return node.DeepClone();
        }
    }

    private static List<string> Diff(JsonNode? left, JsonNode? right, string path = "$")
    {
        var result = new List<string>();
        if (left is JsonObject lo && right is JsonObject ro)
        {
            foreach (var key in lo.Select(p => p.Key).Union(ro.Select(p => p.Key)))
            {
                if (!lo.ContainsKey(key)) result.Add($"{path}.{key} only in identity");
                else if (!ro.ContainsKey(key)) result.Add($"{path}.{key} only in monolith");
                else result.AddRange(Diff(lo[key], ro[key], $"{path}.{key}"));
            }
        }
        else if (left is JsonArray la && right is JsonArray ra)
        {
            if (la.Count != ra.Count) result.Add($"{path} length monolith={la.Count} identity={ra.Count}");
            for (var i = 0; i < Math.Min(la.Count, ra.Count); i++)
                result.AddRange(Diff(la[i], ra[i], $"{path}[{i}]"));
        }
        else if ((left?.ToJsonString() ?? "null") != (right?.ToJsonString() ?? "null"))
        {
            result.Add($"{path}: monolith={left?.ToJsonString() ?? "null"} identity={right?.ToJsonString() ?? "null"}");
        }
        return result;
    }

    private sealed record TokenResult(string AccessToken, string? Subject);
}
