using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace FormaWire.Web;

/// <summary>
/// How the editor page runs in this host. The page is shared with FormaWire.Wasm, which
/// defines the same type and runs it in the browser instead.
/// </summary>
public static class EditorRenderMode
{
    public static IComponentRenderMode Mode => RenderMode.InteractiveServer;
}
