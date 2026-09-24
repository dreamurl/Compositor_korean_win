namespace Compositor_korean_win.Core;

/// <summary>Sets a text layer's pixels from its recipe and places them on the document.</summary>
/// <remarks>
/// Shared by the Type tool and by automation, so words set either way land in the same place and
/// behave the same when set again.
/// </remarks>
public static class TextPlacement
{
    /// <summary>
    /// <paramref name="layer"/> set from <paramref name="recipe"/>: new pixels, and a placement that
    /// keeps the text's anchor where it was — or puts it at <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// A layer that has been scaled or rotated keeps its scale and rotation; only its size follows the
    /// new pixels. The anchor is found on the document through the old placement and the new one is
    /// moved until the anchor lands on the same point, which works for any rotation or flip because
    /// a placement's origin only ever translates. The caller owns the new pixels.
    /// </remarks>
    public static ImageLayer Set(ImageLayer layer, LayerText recipe, IGlyphSource glyphs, Point? anchor)
    {
        RenderedText rendered = TextRendering.Render(recipe, glyphs);
        PixelBuffer pixels = rendered.Pixels;

        double scaleX = 1, scaleY = 1;
        LayerTransform current = layer.Transform;
        Point target;
        if (anchor is Point given)
        {
            target = given;
            current = new LayerTransform(Point.Zero, new Size(1, 1)) { Sampling = layer.Transform.Sampling };
        }
        else if (layer.Image is PixelBuffer old && layer.Text is LayerText previous)
        {
            scaleX = current.Size.Width / old.Width;
            scaleY = current.Size.Height / old.Height;
            target = LayerGeometry.ToDocument(current, new Point(previous.AnchorX, previous.AnchorY), old.Width, old.Height);
        }
        else
        {
            target = current.Origin;
        }

        LayerTransform placed = current with
        {
            Origin = Point.Zero,
            Size = new Size(pixels.Width * scaleX, pixels.Height * scaleY),
        };
        Point landed = LayerGeometry.ToDocument(placed, rendered.Anchor, pixels.Width, pixels.Height);
        placed = placed with { Origin = new Point(target.X - landed.X, target.Y - landed.Y) };

        return layer with
        {
            Image = pixels,
            Transform = placed,
            Text = recipe with { AnchorX = rendered.Anchor.X, AnchorY = rendered.Anchor.Y, Rendered = pixels },
            // Painting on a text layer made it pixels; setting its words again makes it text.
            Shape = null,
        };
    }
}
