using Microsoft.AspNetCore.Http;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class LocalRequestsTests
{
    private static HttpContext Request(string? origin = null, string? fetchSite = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("127.0.0.1", 8077);
        if (origin != null) ctx.Request.Headers["Origin"] = origin;
        if (fetchSite != null) ctx.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        return ctx;
    }

    [Fact] public void OwnOriginIsAccepted() => Assert.True(LocalRequests.SameOrigin(Request(origin: "http://127.0.0.1:8077")));
    [Fact] public void ForeignOriginIsRefused() => Assert.False(LocalRequests.SameOrigin(Request(origin: "http://example.com")));
    [Fact] public void CrossSiteFetchIsRefused() => Assert.False(LocalRequests.SameOrigin(Request(fetchSite: "cross-site")));
    [Fact] public void SameOriginFetchIsAccepted() => Assert.True(LocalRequests.SameOrigin(Request(fetchSite: "same-origin")));
    [Fact] public void ARequestWithoutBrowserHeadersIsAccepted() => Assert.True(LocalRequests.SameOrigin(Request()));

    [Fact]
    public void AForeignRequestGetsA403AndTheActionDoesNotRun()
    {
        bool ran = false;
        var result = LocalRequests.Guarded(Request(origin: "http://example.com"), () => { ran = true; return new ActionResult(true, "done"); });
        Assert.Equal(403, Status(result));
        Assert.False(ran);
    }

    [Fact]
    public void AFailedActionGetsA409AndASuccessfulOneA200()
    {
        Assert.Equal(409, Status(LocalRequests.Guarded(Request(), () => new ActionResult(false, "no"))));
        Assert.Equal(200, Status(LocalRequests.Guarded(Request(), () => new ActionResult(true, "yes"))));
    }

    private static int? Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode;
}
