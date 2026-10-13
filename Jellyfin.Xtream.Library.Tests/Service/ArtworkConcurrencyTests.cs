using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace Jellyfin.Xtream.Library.Tests.Service;
public class ArtworkConcurrencyTests
{
[Fact] public async Task ThousandOperationsUseAndReleaseStripedGates()
{
var tracker=new ArtworkFailureTracker(Path.GetTempPath(),NullLogger.Instance);
var gates=Enumerable.Range(0,1000).Select(i=>tracker.Gate(ArtworkFailureTracker.Hash($"https://example.invalid/{i}"))).ToArray();
Assert.Equal(64,gates.Distinct().Count());
var active=new ConcurrentDictionary<SemaphoreSlim,int>();
int overlaps=0;
await Task.WhenAll(gates.Select(async gate=>{
await gate.WaitAsync();
try { if(active.AddOrUpdate(gate,1,(_,n)=>n+1)!=1) Interlocked.Increment(ref overlaps); await Task.Delay(2); active.AddOrUpdate(gate,0,(_,n)=>n-1); }
finally { gate.Release(); }
}));
Assert.Equal(0,overlaps);
Assert.All(gates.Distinct(),gate=>Assert.Equal(1,gate.CurrentCount));
}
[Fact] public async Task CancelledWaitDoesNotLeakSemaphore()
{
var tracker=new ArtworkFailureTracker(Path.GetTempPath(),NullLogger.Instance);
var gate=tracker.Gate(ArtworkFailureTracker.Hash("https://example.invalid/cancel"));
await gate.WaitAsync();
try { using var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(20)); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>gate.WaitAsync(cts.Token)); }
finally { gate.Release(); }
Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(1)));
gate.Release();
Assert.Equal(1,gate.CurrentCount);
}
[Fact] public void ThousandFailedUrlsPersistAcrossReload()
{
var root=Path.Combine(Path.GetTempPath(),"xtream-stress-"+Guid.NewGuid().ToString("N"));
try {
var tracker=new ArtworkFailureTracker(root,NullLogger.Instance);
Parallel.For(0,1000,i=>{var h=ArtworkFailureTracker.Hash($"https://example.invalid/fail/{i}"); tracker.Record(h,false); tracker.Record(h,false); tracker.Record(h,false);});
var reloaded=new ArtworkFailureTracker(root,NullLogger.Instance);
for(int i=0;i<1000;i++) Assert.True(reloaded.IsBlocked(ArtworkFailureTracker.Hash($"https://example.invalid/fail/{i}")));
Assert.InRange(File.ReadLines(Path.Combine(root,"xtream-library","artwork_failures.jsonl")).Count(),1000,2512);
} finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
}
}
