// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Microsoft.JSInterop.WebAssembly;

namespace Microsoft.AspNetCore.Components.WebAssembly.Services;

internal sealed partial class DefaultWebAssemblyJSRuntime : WebAssemblyJSRuntime
{
    internal static readonly DefaultWebAssemblyJSRuntime Instance = new();

    public ElementReferenceContext ElementReferenceContext { get; }

    [DynamicDependency(nameof(InvokeDotNet))]
    [DynamicDependency(nameof(EndInvokeJS))]
    [DynamicDependency(nameof(BeginInvokeDotNet))]
    [DynamicDependency(nameof(ReceiveByteArrayFromJS))]
    private DefaultWebAssemblyJSRuntime()
    {
        ElementReferenceContext = new WebElementReferenceContext(this);
        JsonSerializerOptions.Converters.Add(new ElementReferenceJsonConverter(ElementReferenceContext));
    }

    public JsonSerializerOptions ReadJsonSerializerOptions() => JsonSerializerOptions;

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static string? InvokeDotNet(
        string? assemblyName,
        string methodIdentifier,
        [JSMarshalAs<JSType.Number>] long dotNetObjectId,
        string argsJson)
    {
        var callInfo = new DotNetInvocationInfo(assemblyName, methodIdentifier, dotNetObjectId, callId: null);
        return DotNetDispatcher.Invoke(Instance, callInfo, argsJson);
    }

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static void EndInvokeJS(string argsJson)
    {
        WebAssemblyCallQueue.Schedule(argsJson, static argsJson =>
        {
            // This is not expected to throw, as it takes care of converting any unhandled user code
            // exceptions into a failure on the Task that was returned when calling InvokeAsync.
            DotNetDispatcher.EndInvokeJS(Instance, argsJson);
        });
    }

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static void BeginInvokeDotNet(string? callId, string assemblyNameOrDotNetObjectId, string methodIdentifier, string argsJson)
    {
        // Figure out whether 'assemblyNameOrDotNetObjectId' is the assembly name or the instance ID
        // We only need one for any given call. This helps to work around the limitation that we can
        // only pass a maximum of 4 args in a call from JS to Mono WebAssembly.
        string? assemblyName;
        long dotNetObjectId;
        if (char.IsDigit(assemblyNameOrDotNetObjectId[0]))
        {
            dotNetObjectId = long.Parse(assemblyNameOrDotNetObjectId, CultureInfo.InvariantCulture);
            assemblyName = null;
        }
        else
        {
            dotNetObjectId = default;
            assemblyName = assemblyNameOrDotNetObjectId;
        }

        var callInfo = new DotNetInvocationInfo(assemblyName, methodIdentifier, dotNetObjectId, callId);
        WebAssemblyCallQueue.Schedule((callInfo, argsJson), static state =>
        {
            // This is not expected to throw, as it takes care of converting any unhandled user code
            // exceptions into a failure on the JS Promise object.
            DotNetDispatcher.BeginInvokeDotNet(Instance, state.callInfo, state.argsJson);
        });
    }

    [JSExport]
    [SupportedOSPlatform("browser")]
    private static void ReceiveByteArrayFromJS(int id, byte[] data)
    {
        DotNetDispatcher.ReceiveByteArray(Instance, id, data);
    }

    /// <inheritdoc />
    protected override Task<Stream> ReadJSDataAsStreamAsync(IJSStreamReference jsStreamReference, long totalLength, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(PullFromJSDataStream.CreateJSDataStream(this, jsStreamReference, totalLength, cancellationToken));

    /// <inheritdoc />
    protected override Task TransmitStreamAsync(long streamId, DotNetStreamReference dotNetStreamReference)
    {
        return TransmitDataStreamToJS.TransmitStreamAsync(this, "Blazor._internal.receiveWebAssemblyDotNetDataStream", streamId, dotNetStreamReference);
    }
}
