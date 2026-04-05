namespace AtlCli;

public class CurlHandler : DelegatingHandler
{
    public CurlHandler() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var parts = new List<string> { "curl -s" };

        // Method (skip for GET since curl defaults to it)
        if (request.Method != HttpMethod.Get)
            parts.Add($"-X {request.Method}");

        // Headers
        if (request.Headers.Authorization is not null)
            parts.Add($"-H 'Authorization: {request.Headers.Authorization}'");

        foreach (var (key, values) in request.Headers)
        {
            if (key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            parts.Add($"-H '{key}: {string.Join(", ", values)}'");
        }

        // Body
        if (request.Content is not null)
        {
            var contentType = request.Content.Headers.ContentType?.ToString();
            if (contentType is not null)
                parts.Add($"-H 'Content-Type: {contentType}'");

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            // Escape single quotes in body
            body = body.Replace("'", "'\\''");
            parts.Add($"-d '{body}'");
        }

        // URL
        parts.Add($"'{request.RequestUri}'");

        Console.WriteLine(string.Join(" \\\n  ", parts));

        // Return an empty response so the caller doesn't throw
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        };
    }
}
