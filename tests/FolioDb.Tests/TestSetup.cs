using System.Runtime.CompilerServices;
using FolioDb.Storage;

namespace FolioDb.Tests;

internal static class TestSetup
{
    // Recycled page images are overwritten before reuse, so any reader still holding one fails loudly.
    [ModuleInitializer]
    internal static void Init() => PageCache.PoisonRecycled = true;
}
