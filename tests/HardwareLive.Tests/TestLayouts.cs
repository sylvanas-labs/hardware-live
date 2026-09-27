using System.Net.Http.Headers;
using System.Text;

namespace HardwareLive.Tests;

internal static class TestLayouts
{
    public const string Valid = """
        {
          "id": "valid_layout",
          "name": "Valid layout",
          "widgets": [
            { "kind": "tile", "size": "M", "ref": { "role": "cpu.temp.control" } },
            { "kind": "chart", "size": "L", "ref": { "id": "/cpu/0/temp/1", "hw": "CPU", "role": "cpu.temp" } }
          ]
        }
        """;

    public static StringContent Json(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return content;
    }

    public static HttpRequestMessage AuthenticatedWrite(
        HttpMethod method,
        string path,
        string token,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-HL-Token", token);
        if (json is not null)
        {
            request.Content = Json(json);
        }

        return request;
    }
}
