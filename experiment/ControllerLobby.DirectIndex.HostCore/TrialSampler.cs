using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ControllerLobby.DirectIndex.Native;

namespace ControllerLobby.DirectIndex.HostCore;

public sealed class TrialSampler : IDisposable
{
    private readonly PinnedXInputReader reader;
    private readonly FileStream stream;
    private readonly CancellationTokenSource stopping=new();
    private readonly Thread thread;
    private EndpointRead latest;
    private Exception? failure;
    private int sequence;
    private readonly object observationGate=new();
    private int frozenMask;
    public EndpointRead Latest=>Volatile.Read(ref latest);
    public Exception? Failure=>Volatile.Read(ref failure);
    public event Action<EndpointRead>? Observed;
    public TrialSampler(PinnedXInputReader reader,string log,string runId,string manifestSha256,bool collectUntilConfirmation=false)
    {
        this.reader=reader;latest=reader.Read();frozenMask=collectUntilConfirmation?-1:latest.Sample.ConnectedMask;
        stream=new FileStream(log,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        Write(new{Kind="open",Schema=1,RunId=runId,ManifestSha256=manifestSha256,QpcFrequency=Stopwatch.Frequency,ProcessId=Environment.ProcessId,Reader=reader.Binding});
        WriteSample(latest);
        thread=new Thread(Loop){IsBackground=true,Name="DirectIndex read-only sampler"};
        // Start explicitly after the selection consumer subscribes, so early
        // samples/edges are not lost between native collection and the model.
    }
    public void Start()=>thread.Start();
    public bool TryFreeze(EndpointRead confirmation,int mask)
    {
        lock(observationGate)
        {
            if(/*guard:freeze-current*/failure!=null||frozenMask>=0||!ReferenceEquals(confirmation,latest)||!confirmation.Sample.Valid||mask is <1 or >15||confirmation.Sample.ConnectedMask!=mask)return false;
            frozenMask=mask;return true;
        }
    }

    private void Loop()
    {
        try
        {
            while(!stopping.Token.WaitHandle.WaitOne(20))
            {
                var read=reader.Read();
                lock(observationGate)
                {
                    if (/*guard:sticky-observed-input*/ (!read.Sample.Valid||frozenMask>=0&&read.Sample.ConnectedMask!=frozenMask))
                        Interlocked.CompareExchange(ref failure,new IOException("Observed endpoint topology/read failure"),null);
                    if(read.Qpc<=Latest.Qpc||read.QpcFrequency!=Latest.QpcFrequency)
                        Interlocked.CompareExchange(ref failure,new IOException("Observed endpoint clock failure"),null);
                    WriteSample(read);Volatile.Write(ref latest,read);
                }
                Observed?.Invoke(read);
            }
        }
        catch(Exception e)when(e is not OutOfMemoryException){Interlocked.CompareExchange(ref failure,e,null);}
    }
    private void WriteSample(EndpointRead read)=>Write(new{Kind="sample",Sequence=sequence++,read.Qpc,read.QpcFrequency,read.Slots});
    private void Write<T>(T value)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)+"\n");stream.Write(bytes);stream.Flush(true);
    }
    public bool Fresh
    {
        get
        {
            var read=Latest;long now=Stopwatch.GetTimestamp();
            return Failure==null&&read.QpcFrequency==Stopwatch.Frequency&&now>=read.Qpc&&now-read.Qpc<=Stopwatch.Frequency/4;
        }
    }
    public void Dispose()
    {
        stopping.Cancel();
        if((thread.ThreadState&System.Threading.ThreadState.Unstarted)==0&&!thread.Join(TimeSpan.FromSeconds(5)))throw new IOException("Sampler did not stop; reader remains owned");
        try{Write(new{Kind="close",Samples=sequence,Failed=Failure!=null});}finally{stream.Dispose();stopping.Dispose();}
        // The coordinator disposes the pinned DLL only after this join succeeds.
    }
}
