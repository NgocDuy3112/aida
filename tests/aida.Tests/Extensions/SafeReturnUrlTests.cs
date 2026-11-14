using aida.Extensions;

namespace aida.Tests.Extensions;

public class SafeReturnUrlTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/chat", "/chat")]
    [InlineData("/chat?tab=1", "/chat?tab=1")]
    [InlineData("/a/b/c", "/a/b/c")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("//evil.com", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("http://evil.com/phish", "/")]
    [InlineData("evil.com", "/")]
    [InlineData("\\\\evil.com", "/")]
    public void SafeReturnUrl(string? input, string expected)
    {
        var actual = AuthEndpointExtensions.SafeReturnUrl(input);
        Assert.Equal(expected, actual);
    }
}
