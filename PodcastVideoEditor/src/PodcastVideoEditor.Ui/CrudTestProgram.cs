using PodcastVideoEditor.Core;

Console.WriteLine("🎬 Podcast Video Editor - Database CRUD Test\n");

try
{
    await TestCrud.TestCrudOperations();
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Error: {ex.Message}");
    Console.WriteLine($"Stack: {ex.StackTrace}");
}

Console.WriteLine("\n✅ Test completed!");
