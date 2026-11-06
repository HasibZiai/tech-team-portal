namespace PcServ.Web.Services.Maps;

/// <summary>
/// Backs "dotnet PcServ.Web.dll --render-selftest". PDF drawing relies on native libraries, which
/// can be missing from a slim container image, so CI runs this inside the real image.
/// </summary>
public static class MapRenderSelfTest
{
    public const string CommandLineFlag = "--render-selftest";

    public static int Run()
    {
        try
        {
            var image = MapPdfReader.RenderPdf(SampleMapService.CreatePdf(), includeAnnotations: false);
            Console.WriteLine($"Rendered {image.Width}x{image.Height}, {image.Png.Length / 1024} KB.");

            // The PDF export uses the same native libraries, on a different path through them.
            var layer = new MapLayerView(1, "Self-test", "Custom", "Viewer", "#12315C", true,
                [new MapFeatureView(1, "Marker", .5, .5, "Self-test", PcServ.Core.Entities.MapSymbols.IndoorAccessPoint, null, false)]);
            var map = new MapView(1, "Self-test", "Self-test", null, PcServ.Core.Entities.SiteMapKind.Other, null, "Viewer", image.Width, image.Height, [layer]);
            var pdf = MapPdfExporter.Build(map, image.Png, new HashSet<int> { 1 }, new MapArea(.25, .25, .75, .75), "self-test", "today");
            Console.WriteLine($"Exported a {pdf.Length / 1024} KB PDF.");

            return image.Width > 0 && image.Png.Length > 0 && pdf.Length > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Map rendering failed: {ex}");
            return 1;
        }
    }
}
