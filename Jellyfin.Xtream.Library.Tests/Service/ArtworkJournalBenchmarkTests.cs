using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
namespace Jellyfin.Xtream.Library.Tests.Service;
public class ArtworkJournalBenchmarkTests
{
private readonly ITestOutputHelper _output;
public ArtworkJournalBenchmarkTests(ITestOutputHelper output) => _output=output;
[Theory]
[InlineData(5000)]
[InlineData(15000)]
public void JournalPerformance(int count)
{
var root=Path.Combine(Path.GetTempPath(),"xtream-bench-"+Guid.NewGuid().ToString("N"));
try {
var tracker=new ArtworkFailureTracker(root,NullLogger.Instance);
var timer=Stopwatch.StartNew();
for(int i=0;i<count;i++) tracker.Record(ArtworkFailureTracker.Hash("benchmark-"+i),false);
timer.Stop();
var file=Path.Combine(root,"xtream-library","artwork_failures.jsonl");
var rows=File.ReadLines(file).Count();
Assert.Equal(count,rows);
_output.WriteLine($"URLs={count} DurationMs={timer.ElapsedMilliseconds} JournalBytes={new FileInfo(file).Length} Rows={rows}");
} finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
}
}
