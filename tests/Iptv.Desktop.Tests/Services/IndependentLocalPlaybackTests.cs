using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests.Services;

public sealed class IndependentLocalPlaybackTests
{
 [Fact]
 public async Task ValidLocalMarkerRoutesButSpoofedPolicyAndSavedMarkerCollisionNeverStart()
 {
  var file=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"independent-local-% # ü-"+Guid.NewGuid().ToString("N")+".mp4"));
  await File.WriteAllBytesAsync(file,new byte[]{1,2,3});
  try{
   var library=new MemoryOnlyLibraryService();var policy=new CoreProxyPolicyService(library);var starts=0;
   var local=LocalMediaService.CreateChannel(file);
   Assert.Equal(Path.GetFileName(file),local.Name);Assert.Equal(file,new Uri(local.StreamUrl).LocalPath);
   using var player=new LibVlcPlayerSession(_=>{starts++;return true;},policy.CheckNativePlaybackAsync);
   await player.PlayAsync(local);Assert.Equal(1,starts);
   foreach(var invalid in new[]{local with{ProxyId=""},local with{ProxyId="p"},local with{Headers=new Dictionary<string,string>{{"User-Agent","synthetic"}}},local with{SourceId="spoofed"},local with{StreamUrl="file://remote/share/fixture.mp4"},local with{StreamUrl="\\\\remote\\share\\fixture.mp4"},local with{StreamUrl="https://remote.invalid/fixture.mp4"}})
   {await player.PlayAsync(invalid);Assert.Equal(1,starts);Assert.Equal(PlayerState.Error,player.State);Assert.DoesNotContain(file,player.ErrorMessage??"");}
   await library.SaveConfigAsync(new SourceConfig(LocalMediaService.SourceMarker,"Marker collision",SourceKind.M3u,"http://fixture.invalid/list"));
   await player.PlayAsync(local);Assert.Equal(1,starts);Assert.Equal(PlayerState.Error,player.State);
   File.Delete(file);await player.PlayAsync(local);Assert.Equal(1,starts);
  }finally{if(File.Exists(file)&&Path.GetFullPath(file)==file&&Path.GetFileName(file).StartsWith("independent-local-",StringComparison.Ordinal))File.Delete(file);}
 }
}
