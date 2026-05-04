using System.Security.Cryptography;
using System.Text;
using KuCoinFuturesReporter.Options;
using Microsoft.Extensions.Options;

namespace KuCoinFuturesReporter.Services;

public sealed class KuCoinAuthHandler(IOptions<KuCoinOptions> options) : DelegatingHandler
{
    private readonly KuCoinOptions _options = options.Value;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret) || string.IsNullOrWhiteSpace(_options.ApiPassphrase))
            throw new InvalidOperationException("KuCoin API settings are empty. Fill KuCoin__ApiKey, KuCoin__ApiSecret and KuCoin__ApiPassphrase.");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var method = request.Method.Method.ToUpperInvariant();
        var endpoint = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var prehash = timestamp + method + endpoint + body;

        request.Headers.Remove("KC-API-KEY");
        request.Headers.Remove("KC-API-SIGN");
        request.Headers.Remove("KC-API-TIMESTAMP");
        request.Headers.Remove("KC-API-PASSPHRASE");
        request.Headers.Remove("KC-API-KEY-VERSION");

        request.Headers.Add("KC-API-KEY", _options.ApiKey);
        request.Headers.Add("KC-API-SIGN", HmacSha256Base64(_options.ApiSecret, prehash));
        request.Headers.Add("KC-API-TIMESTAMP", timestamp);
        request.Headers.Add("KC-API-PASSPHRASE", HmacSha256Base64(_options.ApiSecret, _options.ApiPassphrase));
        request.Headers.Add("KC-API-KEY-VERSION", "2");

        return await base.SendAsync(request, cancellationToken);
    }

    private static string HmacSha256Base64(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}
