using System.Text.Json;
using MangaDexLite.Core.Studio;

internal static class StudioTests
{
    public static async Task RunAsync()
    {
        int count = 0;
        async Task Test(string name, Func<Task> body)
        {
            await body();
            Console.WriteLine("PASS Studio: " + name);
            count++;
        }
        void Check(bool value, string message = "Studio assertion failed")
        {
            if (!value) throw new Exception(message);
        }
        void Invalid(Action body)
        {
            try { body(); } catch (ArgumentException) { return; }
            throw new Exception("Invalid coordinates accepted");
        }
        var pageId = Guid.NewGuid();
        StudioProject Project(string root) => new(1, Guid.NewGuid(), "Test chapter", root, "en", "ru",
            [new(pageId, "001.png", 600, 800, [])]);
        await Test("source pixels stay bounded through move, resize and zoom", () =>
        {
            var bounds = new PixelBounds(50, 100, 200, 80);
            bounds.ValidateWithin(600, 800);
            Check(bounds.MoveTo(590, -10, 600, 800) == new PixelBounds(400, 0, 200, 80));
            Check(bounds.ResizeTo(900, 900, 600, 800) == new PixelBounds(50, 100, 550, 700));
            Check(PixelBounds.FromPoints(250, 180, 50, 100) == bounds);
            Check(bounds.Contains(50, 100) && !bounds.Contains(250, 180));
            var display = bounds.Scale(0.5);
            Check(display == new PixelBounds(25, 50, 100, 40));
            Check(display.Scale(2) == bounds);
            Invalid(() => bounds.MoveTo(double.NaN, 0, 600, 800));
            Check(new PixelBounds(599.5, 10, 0.5, 20).ResizeTo(50, 10, 600, 800).Width == 0.5);
            foreach (var invalid in new[] { bounds with { X = -1 }, bounds with { Width = 0 }, bounds with { X = double.NaN }, bounds with { Height = double.PositiveInfinity }, bounds with { Width = 1000 } })
                Invalid(() => invalid.ValidateWithin(600, 800));
            return Task.CompletedTask;
        });
        await Test("region operations reset approval and mock is cancellable", async () =>
        {
            var operations = new StudioOperations();
            var project = operations.AddRegion(Project(Path.GetTempPath()), pageId, new(10, 20, 200, 70));
            var regionId = project.Pages[0].Regions[0].Id;
            project = operations.UpdateRegion(project, pageId, regionId, new(10, 20, 200, 70), "Hello!", "");
            project = await operations.TranslateRegionAsync(project, pageId, regionId, new MockTranslationProvider(), CancellationToken.None);
            var region = project.Pages[0].Regions[0];
            Check(region.Translation.Contains("MOCK") && region.ReviewStatus == ReviewStatus.NeedsReview && region.TranslationProvider == "mock");
            project = operations.SetReviewStatus(project, pageId, regionId, ReviewStatus.Approved);
            project = operations.UpdateRegion(project, pageId, regionId, region.Bounds, region.SourceText, "Edited translation");
            Check(project.Pages[0].Regions[0].ReviewStatus == ReviewStatus.Draft);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool canceled = false;
            try { await operations.TranslateRegionAsync(project, pageId, regionId, new MockTranslationProvider(), cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled);
            Check(operations.RemoveRegion(project, pageId, regionId).Pages[0].Regions.Count == 0);
        });
        await Test("JSON round trip, optional defaults and invalid-region recovery", () =>
        {
            var operations = new StudioOperations();
            var project = operations.AddRegion(Project(Path.GetTempPath()), pageId, new(12.5, 32, 120, 90));
            string json = StudioProjectStore.Serialize(project);
            using var parsed = JsonDocument.Parse(json);
            Check(parsed.RootElement.GetProperty("schemaVersion").GetInt32() == 1);
            Check(parsed.RootElement.GetProperty("pages")[0].GetProperty("regions")[0].GetProperty("reviewStatus").GetString() == "draft");
            var loaded = StudioProjectStore.Deserialize(json, Path.GetTempPath());
            Check(loaded.CanOverwrite && loaded.Project.Pages[0].Regions[0].Bounds == new PixelBounds(12.5, 32, 120, 90));
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var firstPage = node["pages"]![0]!;
            firstPage["regions"]![0]!["bounds"]!["width"] = -1;
            node["futureOptional"] = "preserve me";
            loaded = StudioProjectStore.Deserialize(node.ToJsonString(), Path.GetTempPath());
            Check(!loaded.CanOverwrite && loaded.Warnings.Count >= 2 && loaded.Project.Pages[0].Regions.Count == 0);
            firstPage.AsObject().Remove("regions");
            node.AsObject().Remove("futureOptional");
            loaded = StudioProjectStore.Deserialize(node.ToJsonString(), Path.GetTempPath());
            Check(loaded.Project.Pages[0].Regions.Count == 0 && loaded.CanOverwrite);
            return Task.CompletedTask;
        });
        await Test("chapter import uses numeric order; save/reload preserves source bytes", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MangaLiteStudio-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                foreach (string file in new[] { "10.png", "2.png", "1.png" }) await File.WriteAllBytesAsync(Path.Combine(root, file), [1, 2, 3, 4]);
                await File.WriteAllTextAsync(Path.Combine(root, ".source.json"), "{}");
                var operations = new StudioOperations();
                var project = await operations.ImportChapterAsync(root, new MetadataFixture(), CancellationToken.None);
                Check(project.Pages.Select(p => p.SourceFile).SequenceEqual(new[] { "1.png", "2.png", "10.png" }));
                var id = project.Pages[0].Id;
                project = operations.AddRegion(project, id, new(15, 25, 180, 100));
                var regionId = project.Pages[0].Regions[0].Id;
                project = operations.UpdateRegion(project, id, regionId, new(15, 25, 180, 100), "Original", "Перевод");
                project = operations.SetReviewStatus(project, id, regionId, ReviewStatus.Approved);
                string path = Path.Combine(root, "chapter.mangalite.json");
                await operations.SaveAsync(project, path, CancellationToken.None);
                var loaded = await operations.LoadAsync(path, CancellationToken.None);
                Check(loaded.Project.SourceDirectory == root && loaded.Project.Pages[0].Id == id);
                Check(loaded.Project.Pages[0].Regions[0].Translation == "Перевод" && loaded.Project.Pages[0].Regions[0].ReviewStatus == ReviewStatus.Approved);
                foreach (string file in new[] { "1.png", "2.png", "10.png" })
                {
                    byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, file));
                    Check(bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
                }
                string disk = await File.ReadAllTextAsync(path);
                Check(JsonDocument.Parse(disk).RootElement.GetProperty("sourceDirectory").GetString() == ".");
                await operations.SaveAsync(project, path, CancellationToken.None);
                bool refused = false;
                try { await operations.SaveAsync(project, Path.Combine(root, "1.png"), CancellationToken.None); }
                catch (ArgumentException) { refused = true; }
                Check(refused);
                await File.WriteAllTextAsync(path, "broken JSON");
                refused = false;
                try { await operations.SaveAsync(project, path, CancellationToken.None); }
                catch (InvalidDataException) { refused = true; }
                Check(refused && await File.ReadAllTextAsync(path) == "broken JSON");
                await File.WriteAllTextAsync(path, StudioProjectStore.Serialize(project with { Id = Guid.NewGuid() }));
                string unrelated = await File.ReadAllTextAsync(path);
                refused = false;
                try { await operations.SaveAsync(project, path, CancellationToken.None); }
                catch (InvalidDataException) { refused = true; }
                Check(refused && await File.ReadAllTextAsync(path) == unrelated);
                Check(!Directory.GetFiles(root, "*.tmp").Any());
            }
            finally { Directory.Delete(root, true); }
        });
        await Test("future schema and path traversal are rejected without empty replacement", () =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(StudioProjectStore.Serialize(Project(Path.GetTempPath())))!;
            node["schemaVersion"] = 2;
            bool rejected = false;
            try { StudioProjectStore.Deserialize(node.ToJsonString(), Path.GetTempPath()); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected);
            node["schemaVersion"] = 1;
            node["pages"]![0]!["sourceFile"] = "../original.png";
            rejected = false;
            try { StudioProjectStore.Deserialize(node.ToJsonString(), Path.GetTempPath()); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected);
            node["pages"]![0]!["sourceFile"] = "001.png";
            node["targetLanguage"] = null;
            var recovered = StudioProjectStore.Deserialize(node.ToJsonString(), Path.GetTempPath());
            Check(!recovered.CanOverwrite && recovered.Project.TargetLanguage == "ru");
            return Task.CompletedTask;
        });
        Console.WriteLine($"{count} Studio tests passed.");
    }

    // The external OS decoder is the only fixture; operations and persistence are real.
    private sealed class MetadataFixture : IImageProcessor
    {
        public Task<ImageInfo> InspectAsync(string sourceFile, CancellationToken ct) => Task.FromResult(new ImageInfo(600, 800));
        public Task<IReadOnlyList<OcrRegion>> RecognizeAsync(string sourceFile, CancellationToken ct) => throw new NotSupportedException();
        public Task InpaintAsync(string sourceFile, IReadOnlyList<PixelBounds> regions, string outputFile, CancellationToken ct) => throw new NotSupportedException();
    }
}
