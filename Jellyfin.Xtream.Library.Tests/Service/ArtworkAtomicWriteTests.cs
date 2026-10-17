using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace Jellyfin.Xtream.Library.Tests.Service;
public class ArtworkAtomicWriteTests
{
[Fact] public async Task SuccessfulWriteLeavesCompleteFileAndNoTemporaryFiles()
{
var root=Path.Combine(Path.GetTempPath(),"xtream-atomic-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {var file=Path.Combine(root,"poster.jpg"); var data=Enumerable.Range(0,4096).Select(i=>(byte)(i%256)).ToArray(); await StrmSyncService.WriteArtworkAtomicallyAsync(file,data,NullLogger.Instance,CancellationToken.None); Assert.Equal(data,File.ReadAllBytes(file)); Assert.Empty(Directory.GetFiles(root,"*.tmp"));}
finally {Directory.Delete(root,true);}
}
[Fact] public async Task PreCancelledWriteLeavesNoArtworkOrTemporaryFiles()
{
var root=Path.Combine(Path.GetTempPath(),"xtream-atomic-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {var file=Path.Combine(root,"poster.jpg"); using var cts=new CancellationTokenSource(); cts.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>StrmSyncService.WriteArtworkAtomicallyAsync(file,new byte[4096],NullLogger.Instance,cts.Token)); Assert.False(File.Exists(file)); Assert.Empty(Directory.GetFiles(root,"*.tmp"));}
finally {Directory.Delete(root,true);}
}
[Fact] public async Task ExistingArtworkIsNotOverwritten()
{
var root=Path.Combine(Path.GetTempPath(),"xtream-atomic-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {var file=Path.Combine(root,"poster.jpg"); var original=new byte[]{1,2,3}; await File.WriteAllBytesAsync(file,original); await StrmSyncService.WriteArtworkAtomicallyAsync(file,new byte[]{4,5,6},NullLogger.Instance,CancellationToken.None); Assert.Equal(original,File.ReadAllBytes(file)); Assert.Empty(Directory.GetFiles(root,"*.tmp"));}
finally {Directory.Delete(root,true);}
}

[Fact]
public async Task ConcurrentWritesToSameDestinationDoNotFail()
{
    var root = Path.Combine(Path.GetTempPath(), "xtream-atomic-race-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var file = Path.Combine(root, "poster.jpg");
        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () => await StrmSyncService.WriteArtworkAtomicallyAsync(file, Enumerable.Repeat((byte)i, 4096).ToArray(), NullLogger.Instance, CancellationToken.None)));
        await Task.WhenAll(tasks);
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.Equal(4096, bytes.Length);
        Assert.All(bytes, value => Assert.Equal(bytes[0], value));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    finally
    {
        Directory.Delete(root, true);
    }
}
}
