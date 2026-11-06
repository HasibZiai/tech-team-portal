using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;

// PDFtoImage and SkiaSharp support the platforms this app runs on (Windows and Linux).
#pragma warning disable CA1416

namespace PcServ.Web.Services.Maps;

/// <summary>A piece of text found on a map. X and Y run 0 to 1 from the top-left of the page as displayed.</summary>
public sealed record ImportedText(string Text, double X, double Y);

public sealed record MapPdfContents(
    /// <summary>Short text items, normally room names.</summary>
    IReadOnlyList<ImportedText> Labels,
    /// <summary>Longer text items, such as the school name and address block.</summary>
    IReadOnlyList<ImportedText> Notes,
    int PageCount,
    /// <summary>Annotations with text that aren't labels (such as measurements), left out of the import.</summary>
    int SkippedAnnotations);

public sealed record RenderedImage(byte[] Png, int Width, int Height);

/// <summary>Thrown when an uploaded file can't be used, with a message safe to show on screen.</summary>
public sealed class MapFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads text out of map PDFs and turns maps into images for the viewer.</summary>
public static class MapPdfReader
{
    /// <summary>Longest side of a base image. Sharp when zoomed in, and still fine for browsers.</summary>
    public const int MaxImageSide = 4800;

    private const int MaxLabelLength = 40;
    private const int ColorThreshold = 30;
    private const int PaleThreshold = 190;

    /// <summary>
    /// Finds the text boxes people added to a map (the "Labeled" maps). Their positions are
    /// reported in the page as it's displayed, so rotated CAD pages come out the right way.
    /// </summary>
    public static MapPdfContents ReadText(byte[] pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf);
            var page = document.GetPage(1);

            var labels = new List<ImportedText>();
            var notes = new List<ImportedText>();
            var skipped = 0;

            foreach (var annotation in page.GetAnnotations())
            {
                var text = annotation.Content?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                // Only free-text boxes are labels. Shapes can carry text too, but that's
                // usually a measurement such as a room's area.
                if (annotation.Type != AnnotationType.FreeText)
                {
                    skipped++;
                    continue;
                }

                var rectangle = annotation.Rectangle;
                var x = Math.Clamp((rectangle.Left + rectangle.Right) / 2 / page.Width, 0, 1);
                var y = Math.Clamp(1 - (rectangle.Bottom + rectangle.Top) / 2 / page.Height, 0, 1);
                var item = new ImportedText(text, x, y);

                // Room names are often on two lines ("RM" over "3"), so length is what separates them
                // from blocks of text such as a school's address.
                if (text.Length <= MaxLabelLength) labels.Add(item);
                else notes.Add(item);
            }

            return new MapPdfContents(labels, notes, document.NumberOfPages, skipped);
        }
        catch (Exception ex) when (ex is not MapFileException)
        {
            throw new MapFileException("That PDF couldn't be read. It may be damaged or password protected.", ex);
        }
    }

    /// <summary>Draws page 1 as an image, leaving out any text boxes that were imported as layer data.</summary>
    public static RenderedImage RenderPdf(byte[] pdf, bool includeAnnotations)
    {
        try
        {
            var size = Conversion.GetPageSize(pdf, page: 0);
            var scale = MaxImageSide / Math.Max(size.Width, size.Height);
            var options = new RenderOptions(
                Width: (int)Math.Round(size.Width * scale),
                Height: (int)Math.Round(size.Height * scale),
                WithAnnotations: includeAnnotations,
                BackgroundColor: SKColors.White);

            using var bitmap = Conversion.ToImage(pdf, page: 0, options: options);
            return Encode(bitmap);
        }
        catch (Exception ex) when (ex is not MapFileException)
        {
            throw new MapFileException("That PDF couldn't be drawn. It may be damaged or password protected.", ex);
        }
    }

    /// <summary>
    /// Draws a map with its colored markings removed, leaving the black linework and text.
    /// Used when those markings have been imported as layers, so a layer that's off truly disappears.
    /// </summary>
    public static RenderedImage RenderPdfWithoutColor(byte[] pdf)
    {
        try
        {
            var size = Conversion.GetPageSize(pdf, page: 0);
            var scale = MaxImageSide / Math.Max(size.Width, size.Height);
            var options = new RenderOptions(Width: (int)Math.Round(size.Width * scale), Height: (int)Math.Round(size.Height * scale), BackgroundColor: SKColors.White);
            using var bitmap = Conversion.ToImage(pdf, page: 0, options: options);

            var pixels = bitmap.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                // Colored markings go, along with the pale ghosts they leave. Linework is much darker than these.
                if (max - min > ColorThreshold || min > PaleThreshold) pixels[i] = SKColors.White;
            }
            bitmap.Pixels = pixels;
            return Encode(bitmap);
        }
        catch (Exception ex) when (ex is not MapFileException)
        {
            throw new MapFileException("That PDF couldn't be drawn. It may be damaged or password protected.", ex);
        }
    }

    /// <summary>Normalizes an uploaded PNG or JPEG, shrinking it if it's larger than the viewer needs.</summary>
    public static RenderedImage RenderImage(byte[] image)
    {
        using var original = SKBitmap.Decode(image)
            ?? throw new MapFileException("That image couldn't be read. Try a PDF, PNG or JPEG file.");

        var longest = Math.Max(original.Width, original.Height);
        if (longest <= MaxImageSide) return Encode(original);

        var scale = (double)MaxImageSide / longest;
        var info = new SKImageInfo((int)Math.Round(original.Width * scale), (int)Math.Round(original.Height * scale));
        using var resized = original.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new MapFileException("That image couldn't be resized.");
        return Encode(resized);
    }

    private static RenderedImage Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new RenderedImage(data.ToArray(), bitmap.Width, bitmap.Height);
    }
}
