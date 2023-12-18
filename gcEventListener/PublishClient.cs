////////////////////////////////////////////////////////////////////////////////
// Module: PublishClient.cs
//
// Notes:
// 
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

namespace DotnetInsights
{

    ////////////////////////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////////////////////

    public class PublishClient
    {
        public bool ProcessDied { get; set; }
        public string ProcessName { get; set; }
        public string ProcessCommandLine { get; set; }
        public int ProcessID { get; set; }
        public EventPipeSession Session { get; set; }

        public DateTime StartTime { get; set; }

        public List<string> Allocations { get; set; }
        public List<string> JittedMethods { get; set; }

        internal ProcessInfo ProcessInfo { get; set; }
        public Action<EventType, string> EventFinishedCallback { get; set; }

        private Dictionary<long, MethodJitInfo> Methods { get; set; }

        private Process Process { get; set; }

        // <summary>
        // There is one PublishClient per process. Unlike the TraceEvent based
        // provider when there is an instance level call, we will already have
        // all the bookkeeping done via the instance call. We only need to add
        // the GC data for this particular process.
        // </summary>
        public PublishClient(int processId, Action<EventType, string> callback)
        {
            this.ProcessID = processId;
            this.Session = null;
            this.ProcessCommandLine = ProcessNameHelper.GetProcessCommandLineForPid(processId);

            this.ProcessDied = false;
            this.Allocations = new List<string>();
            this.JittedMethods = new List<string>();

            this.Methods = new Dictionary<long, MethodJitInfo>();

            if (string.IsNullOrWhiteSpace(this.ProcessCommandLine))
            {
                // Process died.
                this.ProcessDied = true;
                return;
            }

            try
            {
                this.Process = Process.GetProcessById(processId);
                this.StartTime = this.Process.StartTime;
            }
            catch
            {
                this.ProcessDied = true;
            }

            this.ProcessName = ProcessNameHelper.GetProcessNameForPid(processId);

            this.EventFinishedCallback = callback;
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

            if (info.Heaps.Count > 0 && info.Heaps.Count == info.NumHeaps && info.ProcessedPerHeap)
            {
                this.ProcessCurrentGc(info);
            }

            info.ProcessedGcHeapInfo = true;
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

            if (processInfo.CurrentGC.Heaps.Count == processInfo.CurrentGC.NumHeaps && processInfo.CurrentGC.ProcessedGcHeapInfo)
            {
                this.ProcessCurrentGc(processInfo.CurrentGC);
            }

            processInfo.CurrentGC.ProcessedPerHeap = true;
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
            catch (Exception)
            {
                // Most likely the existing process died, and another managed process
                // took its place.
                processInfo.GCs = new Dictionary<int, GcInfo>();

                processInfo.GCs.Add(info.Id, info);
            }

            if (processInfo.CurrentGC != null && processInfo.CurrentGC.NumHeaps != processInfo.CurrentGC.Heaps.Count)
            {
                // We have started processing another gc before finishing the first on
                // This is almost certainly because we are not able to keep up with the amount
                // of incoming events
                Debug.Assert(!processInfo.CurrentGC.ProcessedGcHeapInfo);
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

        private string getReturnData(string jsonInfoString)
        {
            string processName = this.ProcessName;

            if (processName.IndexOf("dotnet") != -1)
            {
                // Change the process name to the first arugment of the command line.
                string[] split = this.ProcessCommandLine.Split(' ');
                Debug.Assert(split.Length > 1);

                // This is a dotnet run command
                if (split[1] == "exec")
                {
                    Debug.Assert(split.Length > 2);
                    processName = Path.GetFileName(split[2]);
                }
                else
                {
                    processName = Path.GetFileName(split[1]);
                }
            }

            if (string.IsNullOrWhiteSpace(processName))
            {
                throw new NotImplementedException();
            }

            this.Process = Process.GetProcessById(this.ProcessID);

            long workingSet = this.Process.WorkingSet64;
            long pagedMemory = this.Process.PagedMemorySize64;
            long privateBytes = this.Process.PrivateMemorySize64;
            long virtualMemory = this.Process.VirtualMemorySize64;
            long nonPagedSystemMemory = this.Process.NonpagedSystemMemorySize64;
            long pagedSystemMemory = this.Process.PagedSystemMemorySize64;

            string commandLine = this.ProcessCommandLine;
            return $"{{\"ProcessID\": {this.ProcessID}, \"ProcessName\": \"{processName}\",\"workingSet\":\"{workingSet}\",\"pagedMemory\":\"{pagedMemory}\",\"privateBytes\":\"{privateBytes}\",\"virtualMemory\":\"{virtualMemory}\",\"processStartTime\":\"{this.StartTime}\",\"nonPagedSystemMemory\":\"{nonPagedSystemMemory}\",\"pagedSystemMemory\":\"{pagedSystemMemory}\",\"currentTime\":\"{DateTime.Now}\",\"processCommandLine\":\"{commandLine}\",\"data\": {jsonInfoString}}}";
        }

        public void OnAllocationTick(GCAllocationTickTraceData data)
        {
            AllocationInfo info = new AllocationInfo();
            info.AllocSizeBytes = data.AllocationAmount64;
            info.HeapIndex = data.HeapIndex;
            info.Kind = data.AllocationKind;
            info.TypeName = data.TypeName;

            string returnData = this.getReturnData(info.ToJsonString());
            this.Allocations.Add(returnData);
        }

        public void OnJitStart(MethodJittingStartedTraceData data)
        {
            // Check to see if this method has already been loaded.
            // If so this should be a tier up
            if (this.Methods.ContainsKey(data.MethodID))
            {
                MethodJitInfo info = this.Methods[data.MethodID];

                // If we are tiering up, assume the method has been quick
                // jitted already
                Debug.Assert(info.HasLoaded);

                if (info.HasLoaded)
                {
                    // This is a rejit. We will just re-write the data
                    info.HasLoaded = false;
                    info.LoadTime = 0;
                    info.Tier = 0;
                    info.isTieredUp = true;

                    info.MethodName = $"{data.MethodNamespace}:{data.MethodSignature}:{data.MethodName}";

                    Debug.Assert(info.Stopwatch != null);
                    info.Stopwatch.Start();
                }
            }
            else
            {
                MethodJitInfo info = new MethodJitInfo();
                info.HasLoaded = false;
                info.LoadTime = 0;
                info.isTieredUp = false;
                info.MethodId = data.MethodID;

                info.Stopwatch = new Stopwatch();
                info.Stopwatch.Start();

                info.MethodName = $"{data.MethodNamespace}:{data.MethodSignature}:{data.MethodName}";

                this.Methods.Add(data.MethodID, info);
            }
        }

        public void MethodLoad(MethodLoadUnloadVerboseTraceData data)
        {
            // Method has had to have been observed for a jit started
            if (!this.Methods.ContainsKey(data.MethodID))
            {
                Debug.Assert(data.OptimizationTier == OptimizationTier.ReadyToRun);
                return;
            }

            MethodJitInfo info = this.Methods[data.MethodID];
            info.Stopwatch.Stop();

            double loadTimeMs = info.Stopwatch.Elapsed.TotalMilliseconds;
            info.LoadTime = loadTimeMs;

            info.Tier = (int)data.OptimizationTier;

            info.HasLoaded = true;

            if (info.isTieredUp)
            {
                info.isTieredUp = false;
            }

            string returnData = this.getReturnData(info.ToJsonString());
            this.EventFinishedCallback(EventType.JitEvent, returnData);
        }

        public void LoadR2RMethodStart(R2RGetEntryPointStartTraceData data)
        {
            MethodJitInfo info = new MethodJitInfo();
            info.HasLoaded = true;
            info.LoadTime = 0;
            info.isTieredUp = false;
            info.MethodId = data.MethodID;
            info.Tier = (int)OptimizationTier.ReadyToRun;

            info.Stopwatch = new Stopwatch();
            info.Stopwatch.Start();

            this.Methods.Add(data.MethodID, info);
        }

        public void LoadR2RMethodEnd(R2RGetEntryPointTraceData data)
        {
            // Method has had to have been observed for a jit started
            if (!this.Methods.ContainsKey(data.MethodID))
            {
                Debug.Assert(false);
                return;
            }

            MethodJitInfo info = this.Methods[data.MethodID];
            info.Stopwatch.Stop();

            double loadTimeMs = info.Stopwatch.Elapsed.TotalMilliseconds;
            info.LoadTime = loadTimeMs;

            info.MethodName = $"{data.MethodNamespace}:{data.MethodSignature}:{data.MethodName}";

            string returnData = this.getReturnData(info.ToJsonString());
            this.EventFinishedCallback(EventType.JitEvent, returnData);
        }

        private void ProcessCurrentGc(GcInfo info)
        {
            Debug.Assert(info.Heaps.Count != 0);

            string returnData = this.getReturnData(info.ToJsonString());

            this.EventFinishedCallback(EventType.GcCollection, returnData);
            info.ProcessedGcHeapInfo = true;
            info.ProcessedPerHeap = true;

            if (this.Allocations.Count > 0)
            {
                string allocReturnData = null;

                allocReturnData = $"[{string.Join(",", this.Allocations)}]";
                this.Allocations.Clear();
                this.EventFinishedCallback(EventType.GcAlloc, allocReturnData);
            }
        }

        ////////////////////////////////////////////////////////////////////////////////
        /// Thread event handlers
        ////////////////////////////////////////////////////////////////////////////////
        
        private void OnIOThreadCreate(ThreadPoolIOWorkTraceData data) {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnIOThreadTerminate(ThreadPoolIOWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnIOThreadRetire(ThreadPoolIOWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnIOThreadUnretire(ThreadPoolIOWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadStart(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadStop(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadWait(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadRetirementStart(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadRetirementStop(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadAdjustmentSample(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadAdjustmentAdjustment(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolWorkerThreadAdjustmentStats(ThreadPoolWorkingThreadCountTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolEnqueue(ThreadPoolWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolDequeue(ThreadPoolWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolIOEnqueue(ThreadPoolIOWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolIODequeue(ThreadPoolIOWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadPoolIOPack(ThreadPoolIOWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadCreating(ThreadPoolWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }

        private void OnThreadRunning(ThreadPoolWorkTraceData data)
        {
            // TODO: Complete this...
            throw new NotImplementedException();
        }
    }

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////

} // End of namespace (DotnetInsights)

////////////////////////////////////////////////////////////////////////////////
////////////////////////////////////////////////////////////////////////////////
