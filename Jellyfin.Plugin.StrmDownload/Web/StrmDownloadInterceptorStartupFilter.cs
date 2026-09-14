using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.StrmDownload.Web;

/// <summary>
/// Inserts <see cref="StrmDownloadInterceptorMiddleware"/> at the very front
/// of Jellyfin's ASP.NET Core pipeline so it can intercept download requests
/// before they reach Jellyfin's own controller.
/// </summary>
public class StrmDownloadInterceptorStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<StrmDownloadInterceptorMiddleware>();
            next(app);
        };
    }
}
