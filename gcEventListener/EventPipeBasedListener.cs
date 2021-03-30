////////////////////////////////////////////////////////////////////////////////
// Module: EventListener.cs
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
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Microsoft.Diagnostics.NETCore.Client;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

public class EventPipeBasedListener
{
    public class PublishClient
    {
        public string ProcessCommandLine { get; set; }
        public int ProcessID { get; set; }
        public EventPipeSession Session { get; set; }

        internal ProcessInfo ProcessInfo { get; set; }
        public Action<string> GcFinishedCallback { get; set; }

        // <summary>
        // There is one PublishClient per process. Unlike the TraceEvent based
        // provider when there is an instance level call, we will already have
        // all the bookkeeping done via the instance call. We only need to add
        // the GC data for this particular process.
        // </summary>
        public PublishClient(int processId, Action<string> callback)
        {
            this.ProcessID = processId;
            this.Session = null;
            this.ProcessCommandLine = ProcessNameHelper.GetProcessNameForPid(processId);

            this.GcFinishedCallback = callback;
            this.ProcessInfo = new ProcessInfo(this.ProcessID);
        }

        // <summary>
        // CLR Heap Stats is called once per GC Collection. It normally is the 
        // last event to fire; however, when the process is using Server GC it
        // is possible the PerHeap Event will fire many times after this event.
        // </summary>
        public void OnGCHeapStats(GCHeapStatsTraceData data)
        {
            var processInfo = this.ProcessInfo;

            if (processInfo.CurrentGC == null) return;

            var info = processInfo.CurrentGC;

            info.TotalHeapSize = data.TotalHeapSize;
            info.TotalPromoted = data.TotalPromoted;
            info.GenerationSize0 = data.GenerationSize0;
            info.GenerationSize1 = data.GenerationSize1;
            info.GenerationSize2 = data.GenerationSize2;
            info.GenerationSizeLOH = data.GenerationSize3;
            info.TotalPromotedSize0 = data.TotalPromotedSize0;
            info.TotalPromotedSize1 = data.TotalPromotedSize1;
            info.TotalPromotedSize2 = data.TotalPromotedSize2;
            info.TotalPromotedLOH = data.TotalPromotedSize3;

            if (info.Heaps.Count > 0 && info.Heaps.Count == info.NumHeaps && !info.Processed)
            {
                this.ProcessCurrentGc(info);
            }
        }

        // <summary>
        // This event callback is generally called before PerHeapHistory
        // </summary>
        public void OnGCGlobalHeapHistory(GCGlobalHeapHistoryTraceData data)
        {
            this.ProcessInfo.CurrentGC.NumHeaps = data.NumHeaps;
            this.ProcessInfo.CurrentGC.Gen0MinSize = data.FinalYoungestDesired;
        }

        // <summary>
        // This event callback is called for each GC Heap.
        // </summary>
        public void OnGCPerHeapHistory(GCPerHeapHistoryTraceData data)
        {
            var processInfo = this.ProcessInfo;
            if (processInfo.CurrentGC == null) return;

            var heapInfo = processInfo.CurrentGC.Heaps;

            string heapData = data.ToString();

            string newline = "\n";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                newline = "\r\n";
            }

            var xmlSplit = heapData.Split(newline);

            HeapInfo currentHeap = new HeapInfo();
            for (int index = 0; index < xmlSplit.Length; ++index)
            {
                string currentLine = xmlSplit[index];

                string[] dataSplit = currentLine.Split("GenData ");
                if (dataSplit.Length <= 1) continue;

                Generation currentGen = new Generation();

                string[] kvSplit = dataSplit[1].Split(' ');

                for (int keyIndex = 0; keyIndex < kvSplit.Length; ++keyIndex)
                {
                    string kv = kvSplit[keyIndex];

                    string replacedStr = kv.Replace("\"", "");
                    replacedStr = replacedStr.Replace("/>", "");
                    string[] eSplit = replacedStr.Split('=');

                    string key = eSplit[0];
                    string value = eSplit[1];
                    value = value.Replace(",", "");

                    if (keyIndex == 0)
                    {
                        Debug.Assert(key == "Name");

                        int genId = 0;
                        if (value == "Gen0") genId = 0;
                        else if (value == "Gen1") genId = 1;
                        else if (value == "Gen2") genId = 2;
                        else if (value == "GenLargeObj") genId = 3;
                        else genId = 4;

                        currentGen.Id = genId;
                    }
                    else if (keyIndex == 1)
                    {
                        Debug.Assert(key == "SizeBefore");

                        currentGen.SizeBefore = long.Parse(value);
                    }
                    else if (keyIndex == 2)
                    {
                        Debug.Assert(key == "SizeAfter");

                        currentGen.SizeAfter = long.Parse(value);
                    }
                    else if (keyIndex == 3)
                    {
                        Debug.Assert(key == "ObjSpaceBefore");

                        currentGen.ObjSpaceBefore = long.Parse(value);
                    }
                    else if (keyIndex == 4)
                    {
                        Debug.Assert(key == "Fragmentation");

                        currentGen.Fragmentation = long.Parse(value);
                    }
                    else if (keyIndex == 5)
                    {
                        Debug.Assert(key == "ObjSizeAfter");

                        currentGen.ObjSizeAfter = long.Parse(value);
                    }
                    else if (keyIndex == 6)
                    {
                        Debug.Assert(key == "FreeListSpaceBefore");

                        currentGen.FreeListSpaceBefore = long.Parse(value);
                    }
                    else if (keyIndex == 7)
                    {
                        Debug.Assert(key == "FreeObjSpaceBefore");

                        currentGen.FreeObjSpaceBefore = long.Parse(value);
                    }
                    else if (keyIndex == 8)
                    {
                        Debug.Assert(key == "FreeListSpaceAfter");

                        currentGen.FreeListSpaceAfter = long.Parse(value);
                    }
                    else if (keyIndex == 9)
                    {
                        Debug.Assert(key == "FreeObjSpaceAfter");

                        currentGen.FreeObjSpaceAfter = long.Parse(value);
                    }
                    else if (keyIndex == 10)
                    {
                        Debug.Assert(key == "In");

                        currentGen.In = long.Parse(value);
                    }
                    else if (keyIndex == 11)
                    {
                        Debug.Assert(key == "Out");

                        currentGen.Out = long.Parse(value);
                    }
                    else if (keyIndex == 12)
                    {
                        Debug.Assert(key == "NewAllocation");

                        currentGen.NewAllocation = long.Parse(value);
                    }
                    else if (keyIndex == 13)
                    {
                        Debug.Assert(key == "SurvRate");

                        currentGen.SurvRate = long.Parse(value);
                    }
                    else if (keyIndex == 14)
                    {
                        Debug.Assert(key == "PinnedSurv");

                        currentGen.PinnedSurv = long.Parse(value);
                    }
                    else if (keyIndex == 15)
                    {
                        Debug.Assert(key == "NonePinnedSurv");

                        currentGen.NonePinnedSurv = long.Parse(value);
                    }
                }

                currentHeap.GenData.Add(currentGen);
            }

            processInfo.CurrentGC.Heaps.Add(currentHeap);

            if (processInfo.CurrentGC.Heaps.Count == processInfo.CurrentGC.NumHeaps && !processInfo.CurrentGC.Processed)
            {
                this.ProcessCurrentGc(processInfo.CurrentGC);
            }
        }

        public void OnGCStart(GCStartTraceData data)
        {
            GcInfo info = new GcInfo();
            info.Generation = data.Depth;
            info.Id = data.Count;
            info.PauseDurationMSec = 0;
            info.PauseEndRelativeMSec = 0;
            info.PauseStartRelativeMSec = data.TimeStampRelativeMSec;
            info.Reason = data.Reason;
            info.Type = data.Type;

            info.Kind = info.Generation <= 1 ? GCKind.Ephemeral : GCKind.FullBlocking;

            var processInfo = this.ProcessInfo;

            try
            {
                processInfo.GCs.Add(info.Id, info);
            }
            catch(Exception e)
            {
                // Most likely the existing process died, and another managed process
                // took its place.
                processInfo.GCs = new Dictionary<int, GcInfo>();
                
                processInfo.GCs.Add(info.Id, info);
            }

            if (processInfo.CurrentGC != null && processInfo.CurrentGC.NumHeaps != processInfo.CurrentGC.Heaps.Count)
            {
                // We have started processing another gc before finishing the first on
                Debug.Assert(!processInfo.CurrentGC.Processed);
                Debug.Assert(false);
            }

            processInfo.CurrentGC = info;
        }

        public void OnGCStop(GCEndTraceData data)
        {
            var processInfo = this.ProcessInfo;

            if (processInfo != null)
            {
                GcInfo info = null;
                if (processInfo.GCs.TryGetValue(data.Count, out info))
                {
                    info.PauseEndRelativeMSec = data.TimeStampRelativeMSec;
                    info.PauseDurationMSec = info.PauseEndRelativeMSec - info.PauseStartRelativeMSec;
                }
            }
        }

        private void ProcessCurrentGc(GcInfo info)
        {
            Debug.Assert(info.Heaps.Count != 0);

            string returnData = $"{{\"ProcessID\": {this.ProcessID}, \"ProcessName\": \"{this.ProcessCommandLine}\", \"data\": {info.ToJsonString()}}}";

            this.GcFinishedCallback(returnData);
            info.Processed = true;
        }
    }

    private string ProcessName { get; set; }
    private string SessionName { get; set; }
    private Dictionary<int, ProcessInfo> Processes { get; set; }
    public long ProcessId { get; set; }
    public Dictionary<int, PublishClient> PublishingClients { get; set; }
    public Action<string> GcFinishedCallback { get; set; }

    public EventPipeBasedListener(Action<string> callback, long scopedProcessId = -1)
    {
        this.PublishingClients = new Dictionary<int, PublishClient>();

        IEnumerable<int> processIds = DiagnosticsClient.GetPublishedProcesses();

        foreach (int processId in processIds)
        {
            this.PublishingClients.Add(processId, new PublishClient(processId, callback));
        }

        this.GcFinishedCallback = callback;
    }

    public void Listen()
    {
        foreach (KeyValuePair<int, PublishClient> clientPair in this.PublishingClients)
        {
            this.StartListener(clientPair.Value);
        }

        ParkMainThread().Wait();
    }

    private void StartListener(PublishClient publishClient)
    {
        Task.Run(() => {
            DiagnosticsClient client = new DiagnosticsClient(publishClient.ProcessID);

            List<EventPipeProvider> providers = new List<EventPipeProvider>()
            {
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", System.Diagnostics.Tracing.EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GC)
            };

            try
            {
                using (EventPipeSession session = client.StartEventPipeSession(providers, false))
                {
                    EventPipeEventSource source = new EventPipeEventSource(session.EventStream);

                    publishClient.Session = session;
                    
                    source.Clr.GCHeapStats += publishClient.OnGCHeapStats;
                    source.Clr.GCGlobalHeapHistory += publishClient.OnGCGlobalHeapHistory;
                    source.Clr.GCPerHeapHistory += publishClient.OnGCPerHeapHistory;
                    source.Clr.GCStart += publishClient.OnGCStart;
                    source.Clr.GCStop += publishClient.OnGCStop;

                    Console.WriteLine($"Started listening for: {publishClient.ProcessCommandLine}");

                    try
                    {
                        source.Process();
                    }
                    catch (Exception e)
                    {
                        source.Dispose();
                    }
                }
            }
            catch (Exception e)
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
                PublishClient publishClient = new PublishClient(processId, this.GcFinishedCallback);
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
