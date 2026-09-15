using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Jellyfin.Plugin.StrmDownload.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StrmDownload;

/// <summary>
/// Registers plugin services and the HTTP middleware startup filter.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>
    /// Maximum number of redirects followed when fetching the remote content.
    /// Redirects stay enabled - .strm targets commonly point at a redirecting
    /// front end - but a redirect loop must not be walked indefinitely.
    /// </summary>
    private const int MaxRedirects = 5;

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHttpClient(StrmDownloadInterceptorMiddleware.HttpClientName, ConfigureUpstreamClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = MaxRedirects,

                // This is the default, but it is what makes the identity
                // Accept-Encoding below meaningful: the handler must not
                // negotiate or transparently undo a content coding.
                AutomaticDecompression = DecompressionMethods.None
            });

        serviceCollection.AddSingleton<DownloadConcurrencyLimiter>();
        serviceCollection.AddSingleton<IStartupFilter, StrmDownloadInterceptorStartupFilter>();
    }

    private static void ConfigureUpstreamClient(HttpClient client)
    {
        var version = typeof(PluginServiceRegistrator).Assembly.GetName().Version?.ToString()
            ?? "0.0.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("StrmDownloadProxy", version));

        // Ask the upstream not to encode the body. This is a byte-for-byte proxy
        // that forwards Content-Length, Content-Range and the range headers
        // unchanged; a compressed body would make those describe the encoded
        // length while the client counts decoded bytes.
        client.DefaultRequestHeaders.AcceptEncoding.Add(
            new StringWithQualityHeaderValue("identity"));
    }
}
