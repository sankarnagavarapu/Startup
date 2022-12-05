// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// TODO
/// </summary>
public static class RazorComponentsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// TODO: Move this to an appropriate place
    /// </summary>
    public static void AddRazorComponents(this IServiceCollection services)
    {
        // TODO: It might be possible to add fewer services here
        services
            .AddMvcCore()
            .AddAuthorization()
            .AddDataAnnotations()
            .AddRazorPages();

        services.AddScoped<PassiveComponentRenderer>();
    }

    /// <summary>
    /// TODO
    /// </summary>
    public static void MapRazorComponents(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // TODO: Implement this like MapRazorPages, which is vastly more complex (assuming there are good reasons)
        var entryAssembly = Assembly.GetEntryAssembly()!;
        var componentTypes = entryAssembly.ExportedTypes.Where(t => typeof(IComponent).IsAssignableFrom(t));
        foreach (var componentType in componentTypes)
        {
            if (componentType.GetCustomAttribute<RouteAttribute>() is RouteAttribute routeAttribute)
            {
                endpoints.Map(routeAttribute.Template, httpContext =>
                {
                    var renderer = httpContext.RequestServices.GetRequiredService<PassiveComponentRenderer>();
                    return renderer.HandleRequest(httpContext, componentType);
                });
            }
        }
    }

    class PassiveComponentRenderer
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IViewBufferScope _viewBufferScope;
        private readonly IHttpResponseStreamWriterFactory _writerFactory;
        private readonly HtmlEncoder _htmlEncoder;

        public PassiveComponentRenderer(
            ILoggerFactory loggerFactory,
            IViewBufferScope viewBufferScope,
            IHttpResponseStreamWriterFactory writerFactory,
            HtmlEncoder htmlEncoder)
        {
            _loggerFactory = loggerFactory;
            _viewBufferScope = viewBufferScope;
            _writerFactory = writerFactory;
            _htmlEncoder = htmlEncoder;
        }

        public async Task HandleRequest(HttpContext httpContext,  Type componentType)
        {
            using var htmlRenderer = new HtmlRenderer(httpContext.RequestServices, _loggerFactory, _viewBufferScope);
            var staticComponentRenderer = new StaticComponentRenderer(htmlRenderer);

            var routeData = httpContext.GetRouteData();
            var rootComponentType = typeof(RouteView);
            var rootComponentParameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                { nameof(RouteView.RouteData), new Components.RouteData(componentType, routeData.Values!) },
            });

            var result = await staticComponentRenderer.PrerenderComponentAsync(
                rootComponentParameters,
                httpContext,
                rootComponentType,
                awaitQuiescence: false);

            var viewBuffer = new ViewBuffer(_viewBufferScope, nameof(RazorComponentsEndpointRouteBuilderExtensions), ViewBuffer.ViewPageSize);
            viewBuffer.AppendHtml(result);

            using var writer = _writerFactory.CreateWriter(httpContext.Response.BodyWriter.AsStream(), Encoding.UTF8);
            await viewBuffer.WriteToAsync(writer, _htmlEncoder);

            await foreach (var batch in htmlRenderer.StreamingRenderBatches.ReadAllAsync(httpContext.RequestAborted))
            {
                // TODO: Instead of passing 'result', we should only pass 'batch', and WriteDiffAsync should
                // render that batch to the output instead of the whole page
                await WriteDiffAsync(httpContext, result);
            }
        }

        private async Task WriteDiffAsync(HttpContext httpContext, IHtmlContent result)
        {
            // TODO: Instead of re-rendering the entire page as HTML, just emit the edits in this batch
            // and have client-side JS apply it to the existing DOM.

            var viewBuffer = new ViewBuffer(_viewBufferScope, nameof(RazorComponentsEndpointRouteBuilderExtensions), ViewBuffer.ViewPageSize);
            viewBuffer.AppendHtml(result);

            // Convert to a JSON string. This demo implementation is very unrealistic. A real implementation
            // would not do anything like this.
            using var memoryStream = new MemoryStream();
            using var streamWriter = new StreamWriter(memoryStream);
            await viewBuffer.WriteToAsync(streamWriter, _htmlEncoder);
            await streamWriter.FlushAsync();
            memoryStream.Position = 0;
            using var streamReader = new StreamReader(memoryStream);
            var htmlString = streamReader.ReadToEnd();
            var htmlStringJson = JsonSerializer.Serialize(htmlString);

            using var writer = _writerFactory.CreateWriter(httpContext.Response.BodyWriter.AsStream(), Encoding.UTF8);
            await writer.WriteAsync("\n<script>(function() { const newHtml = ");
            await writer.WriteAsync(htmlStringJson);
            await writer.WriteAsync("; document.body.innerHTML = new DOMParser().parseFromString(newHtml, 'text/html').querySelector('body').innerHTML;");
            await writer.WriteAsync("})()</script>");
        }
    }
}