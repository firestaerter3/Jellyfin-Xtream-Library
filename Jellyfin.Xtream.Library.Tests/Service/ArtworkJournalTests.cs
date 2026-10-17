using System;
using System.IO;
using System.Linq;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;
namespace Jellyfin.Xtream.Library.Tests.Service;
public class ArtworkJournalTests
{
[Fact]
public void CompactionRemovesExpiredEntriesAndStaleTempFile()
{
var root=Path.Combine(Path.GetTempPath(),"artwork-journal-"+Guid.NewGuid().ToString("N"));
try {
var dir=Path.Combine(root,"xtream-library"); Directory.CreateDirectory(dir);
var file=Path.Combine(dir,"artwork_failures.jsonl");
var expired=ArtworkFailureTracker.Hash("expired"); var active=ArtworkFailureTracker.Hash("active");
File.WriteAllLines(file,new[]{JsonConvert.SerializeObject(new ArtworkFailureTracker.Entry{Hash=expired,Failures=3,RetryAfterUtc=DateTime.UtcNow.AddDays(-1)}),JsonConvert.SerializeObject(new ArtworkFailureTracker.Entry{Hash=active,Failures=3,RetryAfterUtc=DateTime.UtcNow.AddDays(1)})});
File.WriteAllText(file+".tmp","interrupted write");
var tracker=new ArtworkFailureTracker(root,NullLogger.Instance);
for(int i=0;i<520;i++) tracker.Record(ArtworkFailureTracker.Hash("failed-repeat"),false);
Assert.False(File.Exists(file+".tmp")); Assert.DoesNotContain(expired,File.ReadAllText(file)); Assert.InRange(File.ReadLines(file).Count(),2,20);
var reloaded=new ArtworkFailureTracker(root,NullLogger.Instance);
Assert.True(reloaded.IsBlocked(active)); Assert.False(reloaded.IsBlocked(expired));
} finally {if(Directory.Exists(root)) Directory.Delete(root,true);}
}
}
