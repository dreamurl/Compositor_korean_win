using System.Text.Json;
using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// The hand tools and the selection over MCP, as a model uses them to retouch rather than only
/// assemble: strokes, Liquify, warp and distort, selections, masks, adjustments per channel,
/// looking closer, and batches.
/// </summary>
public sealed class McpEditingTests : IDisposable
{
    private sealed class TestServices() : BasicEditorServices(new BoxGlyphs())
    {
        public override IReadOnlyList<string> FontFamilies() => ["Box Sans"];
    }

    private readonly int _liveBefore;
    private readonly EditorSession _session;
    private readonly McpServer _server;
    private int _id;

    public McpEditingTests()
    {
        EffectRendering.ClearCache();
        _liveBefore = PixelBuffer.LiveCount;
        _session = new EditorSession(new TestServices());
        _server = new McpServer(new McpTools(_session), "test");
    }

    public void Dispose()
    {
        _session.Dispose();
        foreach (PixelBuffer look in _looks) look.Release();
    }

    // ---- The report that started this: grain in a group --------------------------------------

    [Fact]
    public void GrainInsideAGroupLeavesItsTextShowing()
    {
        Call("new_document", """{"width":200,"height":100,"background":"#000000"}""");
        string left = Id(Call("add_text", """{"text":"AB","x":10,"y":60,"size":40,"color":"#FFFFFF","name":"left"}"""));
        string right = Id(Call("add_text", """{"text":"CD","x":110,"y":60,"size":40,"color":"#FFFFFF","name":"right"}"""));
        int leftBefore = Bright(Look("""{"background":"black"}"""), 0, 100), rightBefore = Bright(Look("""{"background":"black"}"""), 100, 200);
        Assert.True(leftBefore > 200 && rightBefore > 200, "the words should show before anything is grouped");

        Call("group_layers", $$"""{"layers":["{{left}}","{{right}}"],"name":"words"}""");
        Call("add_adjustment", """{"kind":"grain","into":"words","amount":40}""");

        // It sits at the top of the group, over both words, and both still show.
        CanvasDocument document = _session.Current!.Document;
        Guid folder = EditorSession.Layer(document, "words").Id;
        List<ImageLayer> members = [.. document.Layers.Where(layer => layer.ParentId == folder)];
        Assert.Equal(3, members.Count);
        Assert.NotNull(members[^1].Adjustment);

        PixelBuffer shown = Look("""{"background":"black"}""");
        Assert.True(Bright(shown, 0, 100) > leftBefore / 2, "grain in the group should not hide the left word");
        Assert.True(Bright(shown, 100, 200) > rightBefore / 2, "grain in the group should not hide the right word");
    }

    [Fact]
    public void ALayerPutInsideAClippingGroupJoinsIt()
    {
        Call("new_document", """{"width":100,"height":100,"background":"#000000"}""");
        string plate = Id(Call("add_shape", """{"kind":"rectangle","x":10,"y":10,"width":60,"height":60,"fill":"#FFFFFF","name":"plate"}"""));
        string words = Id(Call("add_text", """{"text":"AB","x":0,"y":60,"size":60,"color":"#FF0000","name":"words"}"""));
        Call("set_clipping", $$"""{"layer":"{{words}}"}""");

        // Between the base and what is clipped to it: the new layer joins, and the text stays clipped.
        Call("add_adjustment", $$"""{"kind":"grain","above":"{{plate}}","amount":20}""");
        CanvasDocument document = _session.Current!.Document;
        ImageLayer grain = document.Layers.Single(layer => layer.Adjustment is not null);
        Assert.Equal(Guid.Parse(plate), grain.MaskSourceId);
        Assert.Equal(Guid.Parse(plate), document.Layer(Guid.Parse(words))!.MaskSourceId);

        // Red where the words cross the plate; nothing of them outside it.
        PixelBuffer shown = Look("""{"background":"black"}""");
        (int r, int g, _) = Pixel(shown, 20, 50);
        Assert.True(r > 150 && g < 100, $"the clipped words should show on the plate, was {r},{g}");
        (r, _, _) = Pixel(shown, 5, 50);
        Assert.True(r < 30, "the words should not show off the plate");
    }

    [Fact]
    public void IntoPutsTheLayerAtTheTopOfTheGroupWhereverTheListContinues()
    {
        Call("new_document", """{"width":50,"height":50,"background":"transparent"}""");
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":10,"height":10,"name":"a"}""");
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":10,"height":10,"name":"b"}""");
        Call("group_layers", """{"layers":["a","b"],"name":"pair"}""");
        Call("add_shape", """{"kind":"rectangle","x":20,"y":20,"width":10,"height":10,"name":"c"}""");
        Call("add_shape", """{"kind":"rectangle","x":20,"y":20,"width":10,"height":10,"name":"d"}""");
        Call("arrange_layer", """{"layer":"pair","to":"top"}""");

        Call("add_layer", """{"into":"pair","name":"inside"}""");
        CanvasDocument document = _session.Current!.Document;
        Guid pair = EditorSession.Layer(document, "pair").Id;
        Assert.Equal("inside", document.Layers.Last(layer => layer.ParentId == pair).Name);
    }

    // ---- Selection -----------------------------------------------------------------------------

    [Fact]
    public void ASelectionLimitsFillsAndCanBeInvertedFeatheredAndMatched()
    {
        Call("new_document", """{"width":100,"height":100,"background":"transparent"}""");
        Call("add_layer", """{"name":"paint"}""");

        Assert.Contains("30×30", Text(Call("select", """{"shape":"rectangle","x":10,"y":10,"width":30,"height":30}""")));
        Call("fill_selection", """{"layer":"paint","color":"#FF0000"}""");
        Call("modify_selection", """{"invert":true}""");
        Call("fill_selection", """{"layer":"paint","color":"#0000FF"}""");

        PixelBuffer shown = Look("""{"background":"white"}""");
        (int r, int g, int b) = Pixel(shown, 20, 20);
        Assert.True(r > 240 && b < 20, $"inside the first selection should be red, was {r},{g},{b}");
        (r, _, b) = Pixel(shown, 70, 70);
        Assert.True(b > 240 && r < 20, $"outside it, after inverting, should be blue, was {r},{g},{b}");
        Assert.Contains("\"selection\"", Text(Call("get_document", "{}")));

        // The magic wand finds the red square again, pixel for pixel.
        Assert.Contains("x 10, y 10, 30×30", Text(Call("select", """{"shape":"magic_wand","layer":"paint","x":25,"y":25,"tolerance":10}""")));

        // A copy of it on a layer of its own; a cut leaves a hole.
        string copy = Id(Call("copy_to_layer", """{"layer":"paint","cut":true,"name":"square"}"""));
        Assert.Equal(new PixelRect(10, 10, 30, 30), LayerGeometry.Bounds(_session.Current!.Document.Layer(Guid.Parse(copy))!.Transform));
        Call("select", """{"shape":"none"}""");
        shown = Look("""{"layers":["paint"],"background":"white"}""");
        (r, g, b) = Pixel(shown, 25, 25);
        Assert.True(r > 240 && g > 240 && b > 240, $"the cut should leave the paint layer empty there, was {r},{g},{b}");

        // Feathered, a fill fades across the edge instead of stopping at it.
        Call("add_layer", """{"name":"soft"}""");
        Call("select", """{"shape":"rectangle","x":50,"y":0,"width":50,"height":100,"feather":8}""");
        Call("fill_selection", """{"layer":"soft","color":"#000000"}""");
        shown = Look("""{"layers":["soft"],"background":"white"}""");
        (r, _, _) = Pixel(shown, 50, 50);
        Assert.True(r is > 40 and < 215, $"the feathered edge should be half way, was {r}");
        (r, _, _) = Pixel(shown, 90, 50);
        Assert.True(r < 10, "well inside, the fill is whole");
    }

    // ---- Strokes ------------------------------------------------------------------------------

    [Fact]
    public void BrushEraserAndMaskStrokesFollowThePoints()
    {
        Call("new_document", """{"width":100,"height":100,"background":"#FFFFFF"}""");
        Call("add_layer", """{"name":"ink"}""");
        Call("paint_stroke", """{"layer":"ink","tool":"brush","points":[[10,50],[90,50]],"size":10,"color":"#FF0000"}""");
        PixelBuffer shown = Look("{}");
        Assert.Equal((255, 0, 0), Pixel(shown, 50, 50));
        Assert.Equal((255, 255, 255), Pixel(shown, 50, 20));

        Call("paint_stroke", """{"layer":"ink","tool":"eraser","points":[[50,40],[50,60]],"size":20}""");
        Assert.Equal((255, 255, 255), Pixel(Look("{}"), 50, 50));

        // Kept to the selection: the right half stays untouched.
        Call("select", """{"shape":"rectangle","x":0,"y":0,"width":50,"height":100}""");
        Call("paint_stroke", """{"layer":"ink","tool":"brush","points":[[5,20],[95,20]],"size":8,"color":"#0000FF"}""");
        Call("select", """{"shape":"none"}""");
        shown = Look("{}");
        Assert.Equal((0, 0, 255), Pixel(shown, 25, 20));
        Assert.Equal((255, 255, 255), Pixel(shown, 75, 20));

        // On a mask, black hides: a gap opens through a black block.
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":100,"height":100,"fill":"#000000","name":"block"}""");
        Call("paint_stroke", """{"layer":"block","tool":"brush","mask":true,"points":[[70,0],[70,100]],"size":16,"color":"#000000"}""");
        shown = Look("{}");
        Assert.Equal((255, 255, 255), Pixel(shown, 70, 80));
        Assert.Equal((0, 0, 0), Pixel(shown, 20, 80));
        Assert.Contains("\"mask\"", Text(Call("get_document", "{}")));
    }

    [Fact]
    public void CloneAndHealWorkFromThePicture()
    {
        Call("new_document", """{"width":100,"height":100,"background":"#FFFFFF"}""");
        using (PixelBuffer picture = TwoColours(100, 100))
            Call("add_image", $$"""{"data":"{{Convert.ToBase64String(Png.Encode(picture))}}","x":0,"y":0,"name":"photo"}""");

        // Red copied from the left half onto the blue right half.
        Call("paint_stroke", """{"layer":"photo","tool":"clone","source":{"x":25,"y":50},"points":[[75,50],[80,50]],"size":10}""");
        (int r, _, int b) = Pixel(Look("{}"), 76, 50);
        Assert.True(r > 200 && b < 60, $"the clone should have laid red on the blue, was {r},{b}");

        // A dark spot healed into the blue around it.
        Call("paint_stroke", """{"layer":"photo","tool":"brush","points":[[80,20]],"size":6,"color":"#000000"}""");
        Call("paint_stroke", """{"layer":"photo","tool":"heal","points":[[80,20]],"size":14}""");
        (r, _, b) = Pixel(Look("{}"), 80, 20);
        Assert.True(b > 120, $"healing should have filled the spot from the blue around it, was {r},{b}");
    }

    // ---- Liquify, warp, distort ---------------------------------------------------------------

    [Fact]
    public void LiquifyWarpAndDistortReshapeTheLayer()
    {
        Call("new_document", """{"width":200,"height":200,"background":"#FFFFFF"}""");
        using (PixelBuffer square = Square(200, 200, 80, 120))
            Call("add_image", $$"""{"data":"{{Convert.ToBase64String(Png.Encode(square))}}","x":0,"y":0,"name":"square"}""");
        int before = Dark(Look("{}"));

        Call("liquify", """{"layer":"square","tool":"bloat","points":[[100,100]],"size":120,"pressure":1,"repeat":30}""");
        int swollen = Dark(Look("{}"));
        Assert.True(swollen > before * 1.1, $"bloat should swell the square, {before} → {swollen}");
        Call("undo", "{}");
        Assert.Equal(before, Dark(Look("{}")));

        // Pushed right along the stroke: black reaches past where the square ended.
        Call("liquify", """{"strokes":[{"tool":"forward","points":[[100,100],[150,100]],"size":60,"pressure":1}],"layer":"square"}""");
        (int pushed, _, _) = Pixel(Look("{}"), 125, 100);
        Assert.True(pushed < 128, $"forward should push the square to the right, was {pushed}");

        string shape = Id(Call("add_shape", """{"kind":"rectangle","x":20,"y":20,"width":40,"height":40,"fill":"#00FF00","name":"panel"}"""));
        PixelRect was = LayerGeometry.Bounds(_session.Current!.Document.Layer(Guid.Parse(shape))!.Transform);
        Call("distort_layer", """{"layer":"panel","moves":{"top_left":{"dx":-10,"dy":-10}}}""");
        PixelRect now = LayerGeometry.Bounds(_session.Current!.Document.Layer(Guid.Parse(shape))!.Transform);
        Assert.True(now.X < was.X - 5 && now.Y < was.Y - 5, $"the top-left corner should have moved out, {was} → {now}");
        Assert.Contains("convex", Text(Call("distort_layer", """{"layer":"panel","corners":[[0,0],[10,0],[0,10],[10,10]]}""", expectError: true)));

        Call("warp_layer", """{"layer":"panel","style":"arc","bend":80}""");
        string text = Id(Call("add_text", """{"text":"AB","x":100,"y":150,"size":30,"name":"words"}"""));
        Call("warp_layer", $$"""{"layer":"{{text}}","moves":[{"row":0,"column":0,"dx":-10,"dy":-10}]}""");
        Assert.False(_session.Current!.Document.Layer(Guid.Parse(text))!.IsLiveText);
    }

    // ---- Adjustments and filters ----------------------------------------------------------------

    [Fact]
    public void AdjustmentsTakeEachChannelAndCanBeEdited()
    {
        Call("new_document", """{"width":60,"height":60,"background":"#FFFFFF"}""");
        string levels = Id(Call("add_adjustment", """{"kind":"levels","red":{"output_white":0}}"""));
        Assert.Equal((0, 255, 255), Pixel(Look("{}"), 30, 30));

        Call("edit_adjustment", $$"""{"layer":"{{levels}}","red":{"output_white":255},"blue":{"output_white":0}}""");
        Assert.Equal((255, 255, 0), Pixel(Look("{}"), 30, 30));
        string described = Text(Call("get_document", "{}"));
        Assert.Contains("\"settings\"", described);
        Assert.Contains("\"blue\"", described);
        Call("delete_layers", $$"""{"layers":["{{levels}}"]}""");

        // Reds alone lose their colour; blue keeps it.
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":30,"height":60,"fill":"#FF0000"}""");
        Call("add_shape", """{"kind":"rectangle","x":30,"y":0,"width":30,"height":60,"fill":"#0000FF"}""");
        Call("add_adjustment", """{"kind":"hue_saturation","ranges":{"reds":{"saturation":-100}}}""");
        PixelBuffer shown = Look("{}");
        (int r, int g, int b) = Pixel(shown, 10, 30);
        Assert.True(Math.Abs(r - g) < 20 && Math.Abs(g - b) < 20, $"the reds should be grey, was {r},{g},{b}");
        (r, _, b) = Pixel(shown, 50, 30);
        Assert.True(b > 200 && r < 40, $"blue should stay blue, was {r},{b}");

        // Baked in as a filter: invert, kept to a selection.
        string panel = Id(Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":60,"height":60,"fill":"#FFFFFF"}"""));
        Call("select", """{"shape":"rectangle","x":0,"y":0,"width":30,"height":60}""");
        Call("apply_filter", $$"""{"layer":"{{panel}}","filter":"invert"}""");
        Call("select", """{"shape":"none"}""");
        shown = Look("{}");
        Assert.Equal((0, 0, 0), Pixel(shown, 10, 30));
        Assert.Equal((255, 255, 255), Pixel(shown, 50, 30));
        Call("apply_filter", $$"""{"layer":"{{panel}}","filter":"twirl","angle":90}""");
        Call("apply_filter", $$"""{"layer":"{{panel}}","filter":"levels","output_white":128}""");
    }

    [Fact]
    public void MasksComeFromTheSelectionAndFromPictures()
    {
        Call("new_document", """{"width":100,"height":100,"background":"#000000"}""");
        string panel = Id(Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":100,"height":100,"fill":"#FFFFFF"}"""));

        Call("select", """{"shape":"ellipse","x":20,"y":20,"width":60,"height":60}""");
        Call("set_mask", $$"""{"layer":"{{panel}}","shape":"selection"}""");
        PixelBuffer shown = Look("{}");
        Assert.Equal((255, 255, 255), Pixel(shown, 50, 50));
        Assert.Equal((0, 0, 0), Pixel(shown, 22, 22));
        Call("set_mask", $$"""{"layer":"{{panel}}","invert":true}""");
        Assert.Equal((0, 0, 0), Pixel(Look("{}"), 50, 50));
        Call("select", """{"shape":"none"}""");

        using (PixelBuffer picture = HalfWhite(40, 40))
            Call("set_mask", $$"""{"layer":"{{panel}}","shape":"image","data":"{{Convert.ToBase64String(Png.Encode(picture))}}"}""");
        shown = Look("{}");
        Assert.Equal((255, 255, 255), Pixel(shown, 10, 50));
        Assert.Equal((0, 0, 0), Pixel(shown, 90, 50));

        Call("set_mask", $$"""{"layer":"{{panel}}","feather":10}""");
        (int r, _, _) = Pixel(Look("{}"), 50, 50);
        Assert.True(r is > 40 and < 215, $"the feathered edge should be half way, was {r}");
    }

    // ---- Looking closer --------------------------------------------------------------------------

    [Fact]
    public void RenderZoomsAndShowsChosenLayers()
    {
        Call("new_document", """{"width":100,"height":100,"background":"#FFFFFF"}""");
        Call("add_shape", """{"kind":"rectangle","x":0,"y":0,"width":50,"height":100,"fill":"#FF0000","name":"red"}""");
        Call("add_shape", """{"kind":"rectangle","x":50,"y":0,"width":50,"height":100,"fill":"#0000FF","name":"blue"}""");

        JsonElement zoomed = Call("render", """{"region":{"x":45,"y":0,"width":10,"height":10},"zoom":4}""");
        using (PixelBuffer close = Png.Decode(Image(zoomed)))
        {
            Assert.Equal(40, close.Width);
            Assert.Equal((255, 0, 0), Pixel(close, 2, 2));
            Assert.Equal((0, 0, 255), Pixel(close, 38, 2));
        }
        Assert.Contains("image pixel", Text(zoomed));

        PixelBuffer alone = Look("""{"layers":["blue"],"background":"white"}""");
        Assert.Equal((255, 255, 255), Pixel(alone, 10, 10));
        PixelBuffer hidden = Look("""{"hide":["red"]}""");
        Assert.Equal((255, 255, 255), Pixel(hidden, 10, 10));
        Assert.Equal((0, 0, 255), Pixel(hidden, 90, 10));
    }

    // ---- Batches ------------------------------------------------------------------------------

    [Fact]
    public void ABatchIsOneStepAndAllOrNothing()
    {
        Call("new_document", """{"width":80,"height":80}""");
        int layers = _session.Current!.Document.Layers.Count;

        JsonElement done = Call("batch", """
            {"calls":[
              {"tool":"add_shape","arguments":{"kind":"rectangle","x":5,"y":5,"width":20,"height":20,"name":"one"}},
              {"tool":"apply_filter","arguments":{"layer":"one","filter":"gaussian_blur","radius":2}},
              {"tool":"add_text","arguments":{"text":"AB","x":10,"y":60,"size":20,"name":"two"}},
              {"tool":"render","arguments":{"max_size":40}}
            ]}
            """);
        Assert.Contains("as one step", Text(done));
        Assert.Contains(done.GetProperty("content").EnumerateArray(), part => part.GetProperty("type").GetString() == "image");
        Assert.Equal(layers + 2, _session.Current!.Document.Layers.Count);
        Call("undo", "{}");
        Assert.Equal(layers, _session.Current!.Document.Layers.Count);
        Call("redo", "{}");

        int steps = _session.Current!.History.UndoCount;
        JsonElement failed = Call("batch", """
            {"calls":[
              {"tool":"add_shape","arguments":{"kind":"ellipse","x":5,"y":5,"width":20,"height":20,"name":"three"}},
              {"tool":"update_layer","arguments":{"layer":"no such layer","visible":false}}
            ]}
            """, expectError: true);
        Assert.Contains("call 2 (update_layer)", Text(failed));
        Assert.Equal(layers + 2, _session.Current!.Document.Layers.Count);
        Assert.Equal(steps, _session.Current!.History.UndoCount);
        Assert.Contains("cannot be inside", Text(Call("batch", """{"calls":[{"tool":"undo"}]}""", expectError: true)));

        // Every pixel a batch made and replaced is freed, kept or undone alike.
        Call("close_document", "{}");
        EffectRendering.ClearCache();
        Assert.Equal(_liveBefore, PixelBuffer.LiveCount);
    }

    [Fact]
    public void TextTakesEscapedLineBreaksAndBackslashes()
    {
        Call("new_document", """{"width":200,"height":100}""");
        string broken = Id(Call("add_text", """{"text":"one\\ntwo","x":10,"y":40,"size":20}"""));
        Assert.Equal("one\ntwo", _session.Current!.Document.Layer(Guid.Parse(broken))!.Text!.Text);
        string literal = Id(Call("add_text", """{"text":"C:\\\\new","x":10,"y":90,"size":20}"""));
        Assert.Equal("C:\\new", _session.Current!.Document.Layer(Guid.Parse(literal))!.Text!.Text);
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private readonly List<PixelBuffer> _looks = [];

    /// <summary>A render, decoded; released with the session.</summary>
    private PixelBuffer Look(string arguments)
    {
        PixelBuffer shown = Png.Decode(Image(Call("render", arguments)));
        _looks.Add(shown);
        return shown;
    }

    private static int Bright(PixelBuffer buffer, int left, int right)
    {
        int count = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            ReadOnlySpan<byte> row = buffer.Row(y);
            for (int x = left; x < Math.Min(right, buffer.Width); x++)
                if (row[x * 4] > 128 && row[x * 4 + 1] > 128 && row[x * 4 + 2] > 128) count++;
        }
        return count;
    }

    private static int Dark(PixelBuffer buffer)
    {
        int count = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            ReadOnlySpan<byte> row = buffer.Row(y);
            for (int x = 0; x < buffer.Width; x++)
                if (row[x * 4] < 128) count++;
        }
        return count;
    }

    /// <summary>Left half red, right half blue, opaque.</summary>
    private static PixelBuffer TwoColours(int width, int height) =>
        Painted(width, height, (x, _) => x < width / 2 ? (255, 0, 0, 255) : (0, 0, 255, 255));

    /// <summary>A black square on white.</summary>
    private static PixelBuffer Square(int width, int height, int from, int to) =>
        Painted(width, height, (x, y) => x >= from && x < to && y >= from && y < to ? (0, 0, 0, 255) : (255, 255, 255, 255));

    /// <summary>Left half white, right half black.</summary>
    private static PixelBuffer HalfWhite(int width, int height) =>
        Painted(width, height, (x, _) => x < width / 2 ? (255, 255, 255, 255) : (0, 0, 0, 255));

    private static PixelBuffer Painted(int width, int height, Func<int, int, (int, int, int, int)> colour)
    {
        PixelBuffer buffer = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = buffer.Row(y);
            for (int x = 0; x < width; x++)
            {
                (int r, int g, int b, int a) = colour(x, y);
                row[x * 4] = (byte)r;
                row[x * 4 + 1] = (byte)g;
                row[x * 4 + 2] = (byte)b;
                row[x * 4 + 3] = (byte)a;
            }
        }
        return buffer;
    }

    private JsonElement Call(string tool, string arguments, bool expectError = false)
    {
        string reply = _server.Handle($$"""{"jsonrpc":"2.0","id":{{++_id}},"method":"tools/call","params":{"name":"{{tool}}","arguments":{{arguments}}}}""")!;
        using JsonDocument parsed = JsonDocument.Parse(reply);
        Assert.False(parsed.RootElement.TryGetProperty("error", out JsonElement error), $"{tool}: protocol error {error}");
        JsonElement result = parsed.RootElement.GetProperty("result").Clone();
        bool failed = result.GetProperty("isError").GetBoolean();
        Assert.True(failed == expectError, $"{tool} {(failed ? "failed" : "succeeded")}: {Text(result)}");
        return result;
    }

    private static string Text(JsonElement result) =>
        string.Join("\n", result.GetProperty("content").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "text")
            .Select(part => part.GetProperty("text").GetString()));

    private static byte[] Image(JsonElement result) =>
        result.GetProperty("content").EnumerateArray()
            .First(part => part.GetProperty("type").GetString() == "image")
            .GetProperty("data").GetBytesFromBase64();

    private static string Id(JsonElement result)
    {
        string text = Text(result);
        foreach (string word in text.Split([' ', '\'', '.', ',', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (Guid.TryParse(word, out _)) return word;
        throw new InvalidOperationException("no id in: " + text);
    }

    private static (int, int, int) Pixel(PixelBuffer buffer, int x, int y)
    {
        ReadOnlySpan<byte> row = buffer.Row(y);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
    }
}
