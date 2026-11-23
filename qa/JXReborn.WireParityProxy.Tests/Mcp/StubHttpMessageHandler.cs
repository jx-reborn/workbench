using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace JXReborn.WireParityProxy.Tests.Mcp;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int CallCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(responder(request));
    }
}
