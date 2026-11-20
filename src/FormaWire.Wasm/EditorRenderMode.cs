using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace FormaWire.Web;

/// <summary>How the shared editor page runs in this host: in the browser.</summary>
public static class EditorRenderMode
{
    public static IComponentRenderMode Mode => RenderMode.InteractiveWebAssembly;
}
