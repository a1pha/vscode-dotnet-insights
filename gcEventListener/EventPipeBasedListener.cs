////////////////////////////////////////////////////////////////////////////////
// Module: EventPipeBasedListener.cs
//
// Notes:
// Scope a particular event to a specific process instead of publishing
// information for all the providers on the system.
////////////////////////////////////////////////////////////////////////////////

namespace DotnetInsights {

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Microsoft.Diagnostics.NETCore.Client;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public enum EventType
{
    GcAlloc,
    GcCollection,
    JitEvent,
    ThreadEvent
}

public class EventPipeBasedListener
{
    private string ProcessName { get; set; }
    private string SessionName { get; set; }
    private bool ListenForGcData { get; set; }
    private bool ListenForAllocations { get; set; }
    private bool ListenForJitEvents { get; set; }
    private bool ListenForThreadEvents { get; set; }
    private Dictionary<int, ProcessInfo> Processes { get; set; }
    public long ProcessId { get; set; }
    public Dictionary<int, PublishClient> PublishingClients { get; set; }
    public Action<EventType, string> EventFinishedCallback { get; set; }

    public EventPipeBasedListener(bool listenForGcData, bool listenForAllocations, bool listenForJitEvents, bool listenForThreadEvents, Action<EventType, string> callback, long scopedProcessId = -1)
    {
        this.PublishingClients = new Dictionary<int, PublishClient>();

        IEnumerable<int> processIds = DiagnosticsClient.GetPublishedProcesses();

        foreach (int processId in processIds)
        {
            this.PublishingClients.Add(processId, new PublishClient(processId, callback));
        }

        this.EventFinishedCallback = callback;

        this.ListenForAllocations = listenForAllocations;
        this.ListenForGcData = listenForGcData;
        this.ListenForJitEvents = listenForJitEvents;
        this.ListenForThreadEvents = listenForThreadEvents;
    }

    public void Listen()
    {
        foreach (KeyValuePair<int, PublishClient> clientPair in this.PublishingClients)
        {
            if (!clientPair.Value.ProcessDied)
            {
                this.StartListener(clientPair.Value);
            }
        }

        ParkMainThread().Wait();
    }

    private void StartListener(PublishClient publishClient)
    {
        Task.Run(() => {
            DiagnosticsClient client = new DiagnosticsClient(publishClient.ProcessID);

            // https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/ClrEtwAll.man#L82
            long compilationDiagnosticsKeyword = 0x2000000000;

            List<EventPipeProvider> providers = new List<EventPipeProvider>()
            {
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", System.Diagnostics.Tracing.EventLevel.Verbose, compilationDiagnosticsKeyword | (long)ClrTraceEventParser.Keywords.Jit | (long)ClrTraceEventParser.Keywords.NGen | (long)ClrTraceEventParser.Keywords.GC | (long)ClrTraceEventParser.Keywords.Stack | (long)ClrTraceEventParser.Keywords.Threading),
                // new EventPipeProvider("Microsoft-Windows-DotNETRuntime", System.Diagnostics.Tracing.EventLevel.Verbose, compilationDiagnosticsKeyword | (long)ClrTraceEventParser.Keywords.Jit)
                // new EventPipeProvider("Microsoft-Windows-DotNETRuntime", System.Diagnostics.Tracing.EventLevel.Verbose, (ulong)ClrTraceEventParser.Keywords.Stack)
                // new EventPipeProvider("Microsoft-Windows-DotNETRuntime", System.Diagnostics.Tracing.EventLevel.Verbose, (ulong)ClrTraceEventParser.Keywords.ThreadingKeyword)
            };

            try
            {
                using (EventPipeSession session = client.StartEventPipeSession(providers, false))
                {
                    EventPipeEventSource source = new EventPipeEventSource(session.EventStream);

                    publishClient.Session = session;
                    
                    if (this.ListenForGcData)
                    {
                        source.Clr.GCHeapStats += publishClient.OnGCHeapStats;
                        source.Clr.GCGlobalHeapHistory += publishClient.OnGCGlobalHeapHistory;
                        source.Clr.GCPerHeapHistory += publishClient.OnGCPerHeapHistory;
                        source.Clr.GCStart += publishClient.OnGCStart;
                        source.Clr.GCStop += publishClient.OnGCStop;
                    }
                    
                    if (this.ListenForAllocations)
                    {
                        source.Clr.GCAllocationTick += publishClient.OnAllocationTick;
                    }

                    if (this.ListenForJitEvents)
                    {
                        source.Clr.MethodJittingStarted += publishClient.OnJitStart;
                        source.Clr.MethodLoadVerbose += publishClient.MethodLoad;
                        source.Clr.MethodR2RGetEntryPointStart += publishClient.LoadR2RMethodStart;
                        source.Clr.MethodR2RGetEntryPoint += publishClient.LoadR2RMethodEnd;
                    }

                    if (this.ListenForThreadEvents)
                    {
                        source.Clr.IOThreadCreationStart += publishClient.OnIOThreadCreate;
                        source.Clr.IOThreadCreationStop += publishClient.OnIOThreadTerminate;
                        source.Clr.IOThreadRetirementStart += publishClient.OnIOThreadRetire;
                        source.Clr.IOThreadRetirementStop += publishClient.OnIOThreadUnretire;
                        source.Clr.ThreadPoolWorkerThreadStart += publishClient.OnThreadPoolWorkerThreadStart;
                        source.Clr.ThreadPoolWorkerThreadStop += publishClient.OnThreadPoolWorkerThreadStop;
                        source.Clr.ThreadPoolWorkerThreadWait += publishClient.OnThreadPoolWorkerThreadWait;
                        source.Clr.ThreadPoolWorkerThreadRetirementStart += publishClient.OnThreadPoolWorkerThreadRetirementStart;
                        source.Clr.ThreadPoolWorkerThreadRetirementStop += publishClient.OnThreadPoolWorkerThreadRetirementStop;
                        source.Clr.ThreadPoolWorkerThreadAdjustmentSample += publishClient.OnThreadPoolWorkerThreadAdjustmentSample;
                        source.Clr.ThreadPoolWorkerThreadAdjustmentAdjustment += publishClient.OnThreadPoolWorkerThreadAdjustmentAdjustment;
                        source.Clr.ThreadPoolWorkerThreadAdjustmentStats += publishClient.OnThreadPoolWorkerThreadAdjustmentStats;
                        source.Clr.ThreadPoolEnqueue += publishClient.OnThreadPoolEnqueue;
                        source.Clr.ThreadPoolDequeue += publishClient.OnThreadPoolDequeue;
                        source.Clr.ThreadPoolIOEnqueue += publishClient.OnThreadPoolIOEnqueue;
                        source.Clr.ThreadPoolIODequeue += publishClient.OnThreadPoolIODequeue;
                        source.Clr.ThreadPoolIOPack += publishClient.OnThreadPoolIOPack;
                        source.Clr.ThreadCreating += publishClient.OnThreadCreating;
                        source.Clr.ThreadRunning += publishClient.OnThreadRunning;
                    }

                    Console.WriteLine($"Started listening for: {publishClient.ProcessCommandLine}");

                    try
                    {
                        source.Process();
                    }
                    catch (Exception)
                    {
                        source.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                // The process most likely died in between setting up the event
                // pipe.s
                return;
            }
            
        });
    }

    private void CheckForNewProcessAndListen()
    {
        IEnumerable<int> processIds = DiagnosticsClient.GetPublishedProcesses();

        List<PublishClient> newClients = new List<PublishClient>();
        foreach (int processId in processIds)
        {
            if (!this.PublishingClients.TryGetValue(processId, out PublishClient unused))
            {
                PublishClient publishClient = new PublishClient(processId, this.EventFinishedCallback);
                this.PublishingClients.Add(processId, publishClient);
                newClients.Add(publishClient);
            }
        }

        foreach (PublishClient client in newClients)
        {
            this.StartListener(client);
        }
    }

    private async Task ParkMainThread()
    {
        while (true)
        {
            await Task.Delay(100);
            this.CheckForNewProcessAndListen();
        }
    }
}

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // End of namespace (DotnetInsights)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
