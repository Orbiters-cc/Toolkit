using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    [McpForUnityTool("orbiters_editor_window",
        Description = "List Unity editor windows or capture one window's actual UI to PNG without bringing Unity forward. Background capture is the default; focus=true explicitly permits activation. Inactive docked tabs fail instead of activating. Returns fullPath for image inspection.",
        RequiresPolling = true, PollAction = "status", MaxPollSeconds = 20)]
    public static class EditorWindowScreenshotTool
    {
        public class Parameters
        {
            [ToolParameter("list, capture, or status")] public string action { get; set; }
            [ToolParameter("Window instance ID from list", Required = false)] public int? window_id { get; set; }
            [ToolParameter("Exact full EditorWindow type name; ambiguous open instances require window_id", Required = false)] public string window_type { get; set; }
            [ToolParameter("Explicitly allow opening window_type when absent", Required = false)] public bool open_if_missing { get; set; }
            [ToolParameter("Explicitly permit foreground activation and tab selection; default false", Required = false)] public bool focus { get; set; }
            [ToolParameter("Maximum image edge, 64..4096, or 0 for native pixels; default 0", Required = false)] public int max_resolution { get; set; }
            [ToolParameter("Capture job identifier returned by capture", Required = false)] public string job_id { get; set; }
        }

        private sealed class Job
        {
            internal string Id;
            internal EditorWindow Window;
            internal int MaxResolution;
            internal double Started;
            internal int Ticks;
            internal object Result;
        }

        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        private static Job active;
        private static string latestJobId;

        public static object HandleCommand(JObject args)
        {
            try
            {
                var p = args?.ToObject<Parameters>() ?? throw new ArgumentException("Parameters are required.");
                switch (p.action)
                {
                    case "list":
                        return new SuccessResponse("Open Unity editor windows.", Windows().Select(w => new
                        {
                            windowId = w.GetInstanceID(), windowType = w.GetType().FullName,
                            title = w.titleContent.text, focused = EditorWindow.focusedWindow == w,
                            width = w.position.width, height = w.position.height
                        }).ToArray());
                    case "capture": return Start(p);
                    case "status":
                        // MCP's polling middleware resends the original parameters with action=status.
                        // It does not copy the job_id from the PendingResponse into that request.
                        string id = p.job_id ?? latestJobId;
                        if (id == null || !Jobs.TryGetValue(id, out var job))
                            return new ErrorResponse("Unknown capture job. A domain reload clears jobs; request a new capture.");
                        return job.Result ?? Pending(job);
                    default: return new ErrorResponse("Expected action: list, capture, or status.");
                }
            }
            catch (Exception ex) { return new ErrorResponse(ex.Message); }
        }

        private static EditorWindow[] Windows() => Resources.FindObjectsOfTypeAll<EditorWindow>();

        private static object Start(Parameters p)
        {
            if (Application.isBatchMode) throw new InvalidOperationException("A graphical Unity editor is required.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for Unity compilation and asset refresh to finish.");
            if (active != null) throw new InvalidOperationException("Another window capture is still pending.");
            if (p.max_resolution != 0 && (p.max_resolution < 64 || p.max_resolution > 4096))
                throw new ArgumentException("max_resolution must be 0 or between 64 and 4096.");
            if (p.window_id.HasValue == !string.IsNullOrWhiteSpace(p.window_type))
                throw new ArgumentException("Specify exactly one of window_id and window_type.");

            var matches = Windows().Where(w => p.window_id.HasValue
                ? w.GetInstanceID() == p.window_id.Value : w.GetType().FullName == p.window_type).ToArray();
            if (matches.Length > 1) throw new ArgumentException("Several windows match; select a window_id from list.");
            EditorWindow window = matches.SingleOrDefault();
            if (window == null && p.open_if_missing && !string.IsNullOrWhiteSpace(p.window_type))
            {
                if (!p.focus)
                    throw new InvalidOperationException("Opening a window may activate Unity. Open it yourself first, or explicitly allow focus=true.");
                var type = TypeCache.GetTypesDerivedFrom<EditorWindow>().SingleOrDefault(t => t.FullName == p.window_type);
                if (type == null || type.IsAbstract) throw new ArgumentException("No concrete EditorWindow type matches.");
                window = EditorWindow.GetWindow(type, false, null, p.focus);
            }
            if (window == null) throw new ArgumentException("Window not found. Use list, or explicitly set open_if_missing with window_type.");
            if (p.focus) window.Focus();
            EditorWindowCapture.RepaintWithoutFocus(window);

            foreach (var key in Jobs.Where(pair => pair.Value.Result != null).Select(pair => pair.Key).ToArray())
                if (Jobs.Count >= 16) Jobs.Remove(key);
            var job = new Job { Id = Guid.NewGuid().ToString("N"), Window = window,
                MaxResolution = p.max_resolution, Started = EditorApplication.timeSinceStartup };
            Jobs.Add(job.Id, job);
            latestJobId = job.Id;
            active = job;
            window.Repaint();
            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return Pending(job);
        }

        private static PendingResponse Pending(Job job) => new PendingResponse(
            "Waiting for the selected editor window to repaint.", 0.5, new { job_id = job.Id });

        private static void Tick()
        {
            var job = active;
            if (job == null) { EditorApplication.update -= Tick; return; }
            try
            {
                double elapsed = EditorApplication.timeSinceStartup - job.Started;
                if (elapsed > 10) throw new TimeoutException("Window capture timed out; retry when Unity is responsive.");
                if (job.Window == null) throw new InvalidOperationException("The window closed before capture.");
                // Yield to Unity's event loop; sleeping on the editor thread cannot complete a repaint.
                if (++job.Ticks < 3 || elapsed < 0.35) { job.Window.Repaint(); return; }
                EditorWindowCapture.RepaintWithoutFocus(job.Window);
                job.Result = new SuccessResponse("Editor window screenshot saved.", EditorWindowCapture.Save(job.Window, job.MaxResolution));
            }
            catch (Exception ex) { job.Result = new ErrorResponse(ex.Message); }
            if (job.Result != null) { active = null; EditorApplication.update -= Tick; }
        }
    }
}
