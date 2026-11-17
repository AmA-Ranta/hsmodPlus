using System.IO.Compression;
using System.Text;
using ArenaBoxBot;

var checks=0;
void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
var key=Encoding.ASCII.GetBytes("0123456789ABCDEF");
byte[] Encrypt(byte[] input)
{
    // Independent published RC4 vector generation; no live profile is read.
    var state=Enumerable.Range(0,256).Select(x=>(byte)x).ToArray();var j=0;
    for(var i=0;i<256;i++){j=(j+state[i]+key[i%16])&255;(state[i],state[j])=(state[j],state[i]);}
    var x=0;var y=0;var output=new byte[input.Length];
    for(var i=0;i<input.Length;i++){x=(x+1)&255;y=(y+state[x])&255;(state[x],state[y])=(state[y],state[x]);output[i]=(byte)(input[i]^state[(state[x]+state[y])&255]);}
    return output;
}
string Wire(string json,string compression="",bool corruptChecksum=false)
{
    var bytes=Encoding.UTF8.GetBytes(json);
    if(compression!="")
    {
        using var output=new MemoryStream();
        using(Stream stream=compression=="zlib" ? new ZLibStream(output,CompressionLevel.Optimal,true) : new GZipStream(output,CompressionLevel.Optimal,true))
            stream.Write(bytes,0,bytes.Length);
        bytes=output.ToArray();
        if(corruptChecksum) bytes[^1]^=1;
    }
    return Convert.ToBase64String(Encrypt(bytes));
}
var success="{\"status\":true,\"data\":{\"HERO_08\":{\"hero_rank\":\"A\",\"win_rate\":5000}}}";
var rejected="{\"status\":false,\"msg\":\"key null\",\"ret\":-1}";
for(var cycle=0;cycle<3;cycle++)
{
    foreach(var prefix in new[]{"","\uFEFF"," \r\n\uFEFF "})
    {
        Check(ArenaBoxScoreProtocol.Decode(prefix+rejected,key).Kind==ArenaScoreReplyKind.SessionKeyMissing,"Real key-null response is an explicit rejection");
        Check(ArenaBoxScoreProtocol.Decode(prefix+success,key).Success,"Plain successful JSON");
        Check(ArenaBoxScoreProtocol.Decode(prefix+Wire(success),key).Success,"Legacy encrypted JSON/BOM");
    }
    foreach(var compression in new[]{"zlib","gzip"})
    {
        Check(ArenaBoxScoreProtocol.Decode(Wire(success,compression),key).Success,"Original native compressed response: "+compression);
        Check(!ArenaBoxScoreProtocol.Decode(Wire(rejected,compression),key).Success,"Compressed rejected response remains rejected");
    }
    Check(ArenaBoxScoreProtocol.Decode("\uFEFF{\"status\": false, \"msg\": \"key null\", \"ret\": -1}",key).Kind==ArenaScoreReplyKind.SessionKeyMissing,"Observed47-character error envelope");
    Check(!ArenaBoxScoreProtocol.Decode(Wire(success,"zlib",true),key).Success,"Bad zlib checksum rejected");
    Check(!ArenaBoxScoreProtocol.Decode(Wire(success),Encoding.ASCII.GetBytes("FEDCBA9876543210")).Success,"Wrong16-byte key cannot yield readiness");
    Check(!ArenaBoxScoreProtocol.Decode(Wire("{\"status\":true,\"large\":\""+new string('x',1024*1024)+"\"}","zlib"),key).Success,"Compressed expansion bounded");
    Check(ArenaBoxScoreProtocol.Decode(Wire(rejected),key).Kind==ArenaScoreReplyKind.SessionKeyMissing,"Encrypted rejection remains rejected");
    var falseWithScores="{\"status\":false,\"data\":{\"HERO_08\":{\"hero_rank\":\"S\"}}}";
    Check(!ArenaBoxScoreProtocol.Decode(falseWithScores,key).Success,"Scored data inside rejected response cannot authorize a pick");
    foreach(var invalid in new[]{"", "<html>login</html>", "bad!base64", "{", "{\"data\":{}}", "{\"status\":\"true\"}"})
        Check(!ArenaBoxScoreProtocol.Decode(invalid,key).Success,"Invalid or missing status rejected");
    var urlSafe=Wire(success).Replace('+','-').Replace('/','_').TrimEnd('=');
    Check(ArenaBoxScoreProtocol.Decode(urlSafe,key).Success,"Legacy URL-safe Base64 padding");
    Check(!ArenaBoxScoreProtocol.Decode(Wire(success),new byte[15]).Success,"Wrong key length rejected");
    Check(!ArenaBoxScoreProtocol.Decode(new string('A',2*1024*1024+1),key).Success,"Oversized wire rejected");

    var gate=new ArenaScoringReadinessGate();var now=new DateTime(2026,10,26,1,0,0,DateTimeKind.Utc);var calls=0;
    var first=new TaskCompletionSource<ArenaScoreReply>();
    Task<ArenaScoreReply> Probe(){calls++;return first.Task;}
    Check(!gate.Check("old-profile",now,Probe,out _),"Local file presence does not authorize admission");
    Check(!gate.Check("old-profile",now.AddSeconds(1),Probe,out _) && calls==1,"Pending probe is single flight");
    first.SetResult(new ArenaScoreReply{Kind=ArenaScoreReplyKind.SessionKeyMissing,Detail="key null"});
    Check(!gate.Check("old-profile",now.AddSeconds(2),Probe,out _),"Server key-null cannot open readiness");
    Check(!gate.Check("old-profile",now.AddHours(1),Probe,out _) && calls==1,"Rejected unchanged authentication is not hammered");
    Task<ArenaScoreReply> Good(){calls++;return Task.FromResult(new ArenaScoreReply{Kind=ArenaScoreReplyKind.Success,Detail="ok"});}
    Check(!gate.Check("fresh-profile",now.AddHours(1),Good,out _),"New profile requires its own real probe");
    Check(gate.Check("fresh-profile",now.AddHours(1).AddSeconds(1),Good,out _),"Successful current-profile response authorizes readiness");
    Check(!gate.Check("third-profile",now.AddHours(1).AddSeconds(2),Good,out _),"Profile changes revoke cached readiness");
    var pendingOld=new TaskCompletionSource<ArenaScoreReply>();var changing=new ArenaScoringReadinessGate();
    Check(!changing.Check("a",now,()=>pendingOld.Task,out _),"First profile pending");
    Check(!changing.Check("b",now.AddSeconds(1),Good,out _),"Changed profile cannot use pending old reply");
    pendingOld.SetResult(new ArenaScoreReply{Kind=ArenaScoreReplyKind.Success});
    Check(!changing.Check("b",now.AddSeconds(2),Good,out _),"Successful old-profile reply is discarded");
    Check(changing.Check("b",now.AddSeconds(3),Good,out _),"Only current-profile reply unblocks");
    Check(!changing.Check("b",now.AddSeconds(-1),Good,out _),"Clock rollback revokes cached readiness");
    var faulty=new ArenaScoringReadinessGate();
    Check(!faulty.Check("p",now,()=>Task.FromException<ArenaScoreReply>(new IOException()),out _),"Faulted probe starts closed");
    Check(!faulty.Check("p",now.AddSeconds(1),Good,out _),"Faulted probe cannot authorize readiness");
    Check(!faulty.Check("new",now.AddSeconds(2),Good,out _),"Fresh profile is checked independently after a fault");
    var root=Path.Combine(Path.GetTempPath(),"arena-score-refresh-"+Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var path=Path.Combine(root,"boxai-direct-profile.dpapi");
    var marker=Path.Combine(root,"boxai-direct-profile.invalid");
    try
    {
        var cipher=new byte[]{1,2,3,4};File.WriteAllBytes(path,cipher);
        var refresh=new ArenaScoringProfileRefresh();
        Check(!refresh.Signal(path,ArenaScoringProfileRefresh.Fingerprint(new byte[]{9})),"A changed/new profile cannot be invalidated by an old request");
        Check(!File.Exists(marker),"Stale response creates no refresh marker");
        Check(refresh.Signal(path,ArenaScoringProfileRefresh.Fingerprint(cipher)),"Observed same-profile rejection signals original capture");
        Check(File.ReadAllText(marker)=="probe_rejected\n","Original marker contract retained exactly");
        Check(File.ReadAllBytes(path).SequenceEqual(cipher),"Protected profile contents are never edited");
        File.Delete(marker);
        Check(!refresh.Signal(path,ArenaScoringProfileRefresh.Fingerprint(cipher)),"No capture/restart loop on repeated rejection");
        cipher=new byte[]{5,6,7,8};File.WriteAllBytes(path,cipher);
        Check(!refresh.Signal(path,ArenaScoringProfileRefresh.Fingerprint(cipher)),"No unbounded retry when a replacement is also rejected");
        refresh.AfterVerifiedScore();
        Check(refresh.Signal(path,ArenaScoringProfileRefresh.Fingerprint(cipher)),"A later genuinely successful scoring session rearms expiry recovery");
        var other=new ArenaScoringProfileRefresh();
        Check(!other.Signal(path,ArenaScoringProfileRefresh.Fingerprint(cipher)) && File.ReadAllText(marker)=="probe_rejected\n","Existing native invalidation is preserved");
    }
    finally { if(File.Exists(marker))File.Delete(marker);if(File.Exists(path))File.Delete(path);Directory.Delete(root); }
}
Console.WriteLine($"PASS: Arena scoring envelopes, real key-null rejection, encrypted/compressed compatibility and pre-admission readiness; {checks} assertions / 3 cycles");
