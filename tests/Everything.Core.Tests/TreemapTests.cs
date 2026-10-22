namespace Everything.Core.Tests;

public class TreemapTests
{
    private const double Eps = 1e-9;

    private static bool Inside(TreemapRect inner, TreemapRect outer) =>
        inner.X >= outer.X - Eps && inner.Y >= outer.Y - Eps
        && inner.X + inner.Width <= outer.X + outer.Width + Eps
        && inner.Y + inner.Height <= outer.Y + outer.Height + Eps;

    private static bool Overlap(TreemapRect a, TreemapRect b) =>
        Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X) > 1e-7
        && Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y) > 1e-7;

    private static double Aspect(TreemapRect r) => Math.Max(r.Width / r.Height, r.Height / r.Width);

    [Fact]
    public void Squarify_PaperExample()
    {
        var sizes = new double[] { 6, 6, 4, 3, 2, 2, 1 };
        var bounds = new TreemapRect(0, 0, 6, 4);
        var rects = TreemapLayout.Squarify(sizes, bounds);

        Assert.Equal(sizes.Length, rects.Length);
        for (var i = 0; i < sizes.Length; i++)
        {
            Assert.Equal(sizes[i], rects[i].Area, 1e-9);
            Assert.True(Inside(rects[i], bounds));
            for (var j = i + 1; j < sizes.Length; j++)
                Assert.False(Overlap(rects[i], rects[j]), $"{i} ve {j} çakışıyor");
        }
        Assert.True(rects.Max(Aspect) <= 3.0);
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 1e-9);
    }

    [Fact]
    public void Squarify_AreasProportional_AndFillBounds()
    {
        var rng = new Random(5);
        var sizes = Enumerable.Range(0, 200).Select(_ => rng.NextDouble() * 1000 + 0.01).OrderByDescending(s => s).ToArray();
        var bounds = new TreemapRect(10, 20, 1280, 720);
        var rects = TreemapLayout.Squarify(sizes, bounds);

        var total = sizes.Sum();
        for (var i = 0; i < sizes.Length; i++)
        {
            var expected = sizes[i] / total * bounds.Area;
            Assert.True(Math.Abs(rects[i].Area - expected) <= expected * 1e-6, $"#{i}: {rects[i].Area} != {expected}");
            Assert.True(Inside(rects[i], bounds));
        }
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 1e-6);
    }

    [Fact]
    public void Squarify_NoOverlap_OnSmallInput()
    {
        var sizes = new double[] { 50, 20, 20, 10, 5, 3, 1, 1 };
        var rects = TreemapLayout.Squarify(sizes, new TreemapRect(0, 0, 100, 37));
        for (var i = 0; i < rects.Length; i++)
        {
            for (var j = i + 1; j < rects.Length; j++)
                Assert.False(Overlap(rects[i], rects[j]));
        }
    }

    [Fact]
    public void Squarify_EdgeCases()
    {
        Assert.Empty(TreemapLayout.Squarify(Array.Empty<double>(), new TreemapRect(0, 0, 10, 10)));
        var single = Assert.Single(TreemapLayout.Squarify(new double[] { 3 }, new TreemapRect(1, 2, 10, 5)));
        Assert.Equal(new TreemapRect(1, 2, 10, 5), single);
        Assert.Throws<ArgumentOutOfRangeException>(() => TreemapLayout.Squarify(new double[] { 1, 0 }, new TreemapRect(0, 0, 1, 1)));
    }

    private static FileIndex SampleIndex()
    {
        var index = new FileIndex("/kok");
        var videos = index.AddEntry(FileIndex.RootId, "Videolar", true, 0, 0, false);
        index.AddEntry(videos, "a.mp4", false, 6000, 0, false);
        index.AddEntry(videos, "b.mp4", false, 3000, 0, false);
        var deep = index.AddEntry(videos, "Arşiv", true, 0, 0, false);
        index.AddEntry(deep, "c.mkv", false, 1000, 0, false);
        var docs = index.AddEntry(FileIndex.RootId, "Belgeler", true, 0, 0, false);
        index.AddEntry(docs, "rapor.pdf", false, 2500, 0, false);
        index.AddEntry(docs, "bos.txt", false, 0, 0, false);
        index.AddEntry(FileIndex.RootId, "BosKlasor", true, 0, 0, false);
        for (var i = 0; i < 20; i++)
            index.AddEntry(FileIndex.RootId, $"kucuk{i}.tmp", false, 100, 0, false);
        index.ComputeAggregates();
        return index;
    }

    [Fact]
    public void Build_NestsChildrenInsideDirectories()
    {
        var index = SampleIndex();
        var bounds = new TreemapRect(0, 0, 800, 600);
        var nodes = TreemapBuilder.Build(index, FileIndex.RootId, bounds);
        var byId = nodes.ToDictionary(n => n.Id);

        Assert.DoesNotContain(nodes, n => n.Id == FileIndex.RootId);
        Assert.DoesNotContain(nodes, n => n.Size == 0); // bos.txt ve BosKlasor atlanır
        Assert.Equal(20 + 2 + 5, nodes.Count);

        foreach (var node in nodes)
        {
            Assert.True(Inside(node.Rect, bounds));
            var parent = index.GetParent(node.Id);
            if (parent == FileIndex.RootId)
            {
                Assert.Equal(0, node.Depth);
                continue;
            }
            var p = byId[parent];
            Assert.True(p.IsDirectory);
            Assert.Equal(p.Depth + 1, node.Depth);
            Assert.True(Inside(node.Rect, p.Rect));
            Assert.True(node.Rect.X >= p.Rect.X + 2 - Eps); // dolgu
        }

        var videos = nodes.Single(n => n.Name == "Videolar");
        Assert.Equal("Klasör", videos.Category);
        Assert.Equal("Video", nodes.Single(n => n.Name == "a.mp4").Category);
        Assert.Equal(10_000, videos.Size);

        // Büyük klasörde başlık şeridi bırakılır.
        Assert.True(TreemapBuilder.ShowsHeader(videos.Rect, 14));
        Assert.All(nodes.Where(n => index.GetParent(n.Id) == videos.Id), n => Assert.True(n.Rect.Y >= videos.Rect.Y + 2 + 14 - Eps));
    }

    [Fact]
    public void Build_RespectsMaxNodes_LargestFirst()
    {
        var index = SampleIndex();
        var nodes = TreemapBuilder.Build(index, FileIndex.RootId, new TreemapRect(0, 0, 800, 600), maxNodes: 3);
        Assert.Equal(3, nodes.Count);
        // Belgeler kökte, b.mp4 ise Videolar içinde; ama b.mp4'ün kutusu daha büyük.
        Assert.Equal(new[] { "Videolar", "a.mp4", "b.mp4" }, nodes.Select(n => n.Name));
        Assert.True(nodes.Zip(nodes.Skip(1)).All(p => p.First.Rect.Area >= p.Second.Rect.Area));
    }

    [Fact]
    public void Build_SkipsTinyRects()
    {
        var index = SampleIndex();
        var nodes = TreemapBuilder.Build(index, FileIndex.RootId, new TreemapRect(0, 0, 100, 100), minArea: 100);
        Assert.All(nodes, n => Assert.True(n.Rect.Area >= 100));
        Assert.DoesNotContain(nodes, n => n.Name.StartsWith("kucuk", StringComparison.Ordinal)); // her biri ~79 px²
    }
}
