using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Codex Pet Walker")]
[assembly: AssemblyProduct("Codex Pet Walker")]
[assembly: AssemblyVersion("1.5.5.0")]
[assembly: AssemblyFileVersion("1.5.5.0")]

namespace CodexPetWalker
{
    internal static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        internal const int GwlExStyle = -20;
        internal const long WsExTopmost = 0x00000008L;
        internal const long WsExTransparent = 0x00000020L;
        internal const long WsExToolWindow = 0x00000080L;
        internal const long WsExLayered = 0x00080000L;
        internal const long WsExNoActivate = 0x08000000L;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpNoOwnerZOrder = 0x0200;
        internal const int SwHide = 0;
        internal const int SwShowNoActivate = 8;

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")]
        internal static extern bool SetLayeredWindowAttributes(IntPtr window, int colorKey, byte alpha, int flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
        internal int Width { get { return Right - Left; } }
        internal int Height { get { return Bottom - Top; } }
    }

    internal enum WalkingSpeed { Stopped, Slow, Normal, Fast }
    internal enum BubbleStyle { Speech, Thought }
    internal enum TaskBubblePhase { Hidden, Starting, Working, Completed, AwaitingReview }
    internal enum PetAnimation
    {
        Idle,
        RunningLeft,
        RunningRight,
        Waving,
        Jumping,
        RunningGesture,
        TaskWorking
    }

    internal sealed class AnimatedPetWindow : Form
    {
        internal const int PetWidth = 141;
        internal const int PetHeight = 153;
        private const int SourceCellWidth = 192;
        private const int SourceCellHeight = 208;
        private static readonly int[] IdleDurations = { 1680, 660, 660, 840, 840, 1920 };
        private static readonly int[] RunningDurations = { 120, 120, 120, 120, 120, 120, 120, 220 };
        private static readonly int[] WavingDurations = { 140, 140, 140, 280 };
        private static readonly int[] JumpingDurations = { 140, 140, 140, 140, 280 };
        private static readonly int[] RunningGestureDurations = { 120, 120, 120, 120, 120, 220 };
        private static readonly int[] TaskWorkingDurations = { 600, 600, 600, 600 };
        private static readonly int[] TaskWorkingColumns = { 0, 1, 2, 3 };

        private readonly Bitmap spriteSheet;
        private Bitmap renderedFrame;
        private PetAnimation animation;
        private int frameIndex;
        private DateTime nextFrameAt;
        private bool transientAnimation;
        private int completedLoops;
        private int requestedLoops;
        private bool pointerDown;
        private bool pointerDragging;
        private Point pointerStart;
        private Point previousPointerPosition;
        private Point windowStart;

        internal event EventHandler PetClicked;
        internal event EventHandler PetDragged;

        internal bool IsTransientAnimation { get { return transientAnimation; } }
        internal bool IsBeingDragged { get { return pointerDragging; } }
        internal PetAnimation CurrentAnimation { get { return animation; } }

        internal AnimatedPetWindow(string spriteSheetPath)
        {
            spriteSheet = new Bitmap(spriteSheetPath);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Width = PetWidth;
            Height = PetHeight;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            animation = PetAnimation.Idle;
            nextFrameAt = DateTime.MinValue;
            MouseDown += HandleMouseDown;
            MouseMove += HandleMouseMove;
            MouseUp += HandleMouseUp;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs eventArgs)
        {
            base.OnHandleCreated(eventArgs);
            NativeMethods.SetLayeredWindowAttributes(Handle, 0x00FF00FF, 255, 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= (int)(NativeMethods.WsExLayered | NativeMethods.WsExToolWindow | NativeMethods.WsExTopmost | NativeMethods.WsExNoActivate);
                return parameters;
            }
        }

        internal void SetAnimation(PetAnimation nextAnimation, DateTime now)
        {
            if (transientAnimation && nextAnimation == PetAnimation.Idle) return;
            if (animation == nextAnimation && !transientAnimation) return;
            animation = nextAnimation;
            transientAnimation = false;
            frameIndex = 0;
            nextFrameAt = now;
        }

        internal void PlayTransient(PetAnimation nextAnimation, DateTime now, int loops)
        {
            animation = nextAnimation;
            transientAnimation = true;
            requestedLoops = Math.Max(1, loops);
            completedLoops = 0;
            frameIndex = 0;
            nextFrameAt = now;
        }

        internal void UpdateAnimation(DateTime now)
        {
            if (now < nextFrameAt) return;

            int[] durations = AnimationDurations();
            int logicalFrame = frameIndex % durations.Length;
            int column = animation == PetAnimation.TaskWorking ? TaskWorkingColumns[logicalFrame] : logicalFrame;
            RenderFrame(AnimationRow(), column);
            nextFrameAt = now.AddMilliseconds(durations[logicalFrame]);
            frameIndex++;
            if (frameIndex >= durations.Length)
            {
                frameIndex = 0;
                if (transientAnimation)
                {
                    completedLoops++;
                    if (completedLoops >= requestedLoops)
                    {
                        transientAnimation = false;
                        animation = PetAnimation.Idle;
                        nextFrameAt = now;
                    }
                }
            }
        }

        internal void MovePet(int x, int y)
        {
            if (Left != x || Top != y) Location = new Point(x, y);
        }

        private int AnimationRow()
        {
            if (animation == PetAnimation.RunningRight) return 1;
            if (animation == PetAnimation.RunningLeft) return 2;
            if (animation == PetAnimation.Waving) return 3;
            if (animation == PetAnimation.Jumping) return 4;
            if (animation == PetAnimation.RunningGesture) return 7;
            if (animation == PetAnimation.TaskWorking) return 8;
            return 0;
        }

        private int[] AnimationDurations()
        {
            switch (animation)
            {
                case PetAnimation.RunningLeft:
                case PetAnimation.RunningRight:
                    return RunningDurations;
                case PetAnimation.Waving:
                    return WavingDurations;
                case PetAnimation.Jumping:
                    return JumpingDurations;
                case PetAnimation.RunningGesture:
                    return RunningGestureDurations;
                case PetAnimation.TaskWorking:
                    return TaskWorkingDurations;
                default:
                    return IdleDurations;
            }
        }

        private void RenderFrame(int row, int column)
        {
            Bitmap nextFrame = new Bitmap(PetWidth, PetHeight, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(nextFrame))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.Clear(Color.Magenta);
                graphics.DrawImage(spriteSheet, new Rectangle(0, 0, PetWidth, PetHeight), column * SourceCellWidth, row * SourceCellHeight, SourceCellWidth, SourceCellHeight, GraphicsUnit.Pixel);
            }

            Bitmap previous = renderedFrame;
            renderedFrame = nextFrame;
            Invalidate();
            Update();
            if (previous != null) previous.Dispose();
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            if (renderedFrame != null) eventArgs.Graphics.DrawImageUnscaled(renderedFrame, 0, 0);
        }

        private void HandleMouseDown(object sender, MouseEventArgs eventArgs)
        {
            if (eventArgs.Button != MouseButtons.Left) return;
            pointerDown = true;
            pointerDragging = false;
            pointerStart = Cursor.Position;
            previousPointerPosition = pointerStart;
            windowStart = Location;
            Capture = true;
        }

        private void HandleMouseMove(object sender, MouseEventArgs eventArgs)
        {
            if (!pointerDown) return;
            Point cursor = Cursor.Position;
            int deltaX = cursor.X - pointerStart.X;
            int deltaY = cursor.Y - pointerStart.Y;
            if (!pointerDragging && Math.Abs(deltaX) + Math.Abs(deltaY) >= 5) pointerDragging = true;
            if (pointerDragging)
            {
                int horizontalMovement = cursor.X - previousPointerPosition.X;
                if (horizontalMovement < 0) SetAnimation(PetAnimation.RunningLeft, DateTime.UtcNow);
                else if (horizontalMovement > 0) SetAnimation(PetAnimation.RunningRight, DateTime.UtcNow);
                Location = new Point(windowStart.X + deltaX, windowStart.Y + deltaY);
            }
            previousPointerPosition = cursor;
        }

        private void HandleMouseUp(object sender, MouseEventArgs eventArgs)
        {
            if (eventArgs.Button != MouseButtons.Left || !pointerDown) return;
            pointerDown = false;
            Capture = false;
            if (pointerDragging)
            {
                if (PetDragged != null) PetDragged(this, EventArgs.Empty);
            }
            else
            {
                if (PetClicked != null) PetClicked(this, EventArgs.Empty);
            }
            pointerDragging = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (renderedFrame != null) renderedFrame.Dispose();
                spriteSheet.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TaskBubbleWindow : Form
    {
        private const int BubbleWidth = 320;
        private const int BubbleHeight = 112;
        private readonly Font messageFont;
        private string message;
        private BubbleStyle bubbleStyle;
        private bool tailAtTop;

        internal TaskBubbleWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Width = BubbleWidth;
            Height = BubbleHeight;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            messageFont = new Font("Malgun Gothic", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs eventArgs)
        {
            base.OnHandleCreated(eventArgs);
            NativeMethods.SetLayeredWindowAttributes(Handle, 0x00FF00FF, 255, 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= (int)(NativeMethods.WsExLayered | NativeMethods.WsExToolWindow | NativeMethods.WsExTopmost | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent);
                return parameters;
            }
        }

        internal void ShowMessage(string nextMessage, BubbleStyle nextStyle, AnimatedPetWindow pet)
        {
            bool changed = !string.Equals(message, nextMessage, StringComparison.Ordinal) || bubbleStyle != nextStyle;
            message = nextMessage;
            bubbleStyle = nextStyle;
            PositionNear(pet);
            if (!Visible) Show();
            if (changed) Invalidate();
        }

        internal void PositionNear(AnimatedPetWindow pet)
        {
            Rectangle workingArea = Screen.FromControl(pet).WorkingArea;
            int x = pet.Left + pet.Width / 2 - Width / 2;
            int y = pet.Top - Height + 16;
            bool nextTailAtTop = false;

            if (y < workingArea.Top)
            {
                y = pet.Bottom - 16;
                nextTailAtTop = true;
            }

            x = Math.Max(workingArea.Left, Math.Min(x, workingArea.Right - Width));
            y = Math.Max(workingArea.Top, Math.Min(y, workingArea.Bottom - Height));
            if (tailAtTop != nextTailAtTop)
            {
                tailAtTop = nextTailAtTop;
                Invalidate();
            }
            if (Left != x || Top != y) Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);
            Graphics graphics = eventArgs.Graphics;
            graphics.Clear(Color.Magenta);
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.None;

            Rectangle body = tailAtTop ? new Rectangle(8, 29, Width - 16, 74) : new Rectangle(8, 5, Width - 16, 74);
            Color fillColor = Color.FromArgb(255, 255, 246);
            Color borderColor = Color.FromArgb(41, 54, 79);
            using (SolidBrush fillBrush = new SolidBrush(fillColor))
            using (SolidBrush borderBrush = new SolidBrush(borderColor))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(55, 47, 42)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;

                if (bubbleStyle == BubbleStyle.Thought)
                {
                    DrawPixelCloud(graphics, body, borderBrush, fillBrush);
                    DrawThoughtDots(graphics, borderBrush, fillBrush);
                }
                else
                {
                    DrawPixelRoundedBody(graphics, body, borderBrush, fillBrush);
                    DrawSpeechTail(graphics, body, borderBrush, fillBrush);
                }

                RectangleF textArea = new RectangleF(body.Left + 16, body.Top + 8, body.Width - 32, body.Height - 16);
                graphics.DrawString(message ?? string.Empty, messageFont, textBrush, textArea, format);
            }
        }

        private static void DrawPixelRoundedBody(Graphics graphics, Rectangle body, Brush borderBrush, Brush fillBrush)
        {
            FillPixelRoundedLayer(graphics, borderBrush, body);
            FillPixelRoundedLayer(graphics, fillBrush, Inset(body, 2));
        }

        private static void FillPixelRoundedLayer(Graphics graphics, Brush brush, Rectangle rectangle)
        {
            graphics.FillRectangle(brush, rectangle.Left + 8, rectangle.Top, rectangle.Width - 16, rectangle.Height);
            graphics.FillRectangle(brush, rectangle.Left + 4, rectangle.Top + 4, rectangle.Width - 8, rectangle.Height - 8);
            graphics.FillRectangle(brush, rectangle.Left, rectangle.Top + 8, rectangle.Width, rectangle.Height - 16);
        }

        private static void DrawPixelCloud(Graphics graphics, Rectangle body, Brush borderBrush, Brush fillBrush)
        {
            FillCloudLayer(graphics, borderBrush, body, 0);
            FillCloudLayer(graphics, fillBrush, body, 2);
        }

        private static void FillCloudLayer(Graphics graphics, Brush brush, Rectangle body, int inset)
        {
            Rectangle core = new Rectangle(body.Left + 16, body.Top + 17, body.Width - 32, body.Height - 34);
            graphics.FillRectangle(brush, Inset(core, inset));
            graphics.FillEllipse(brush, Inset(new Rectangle(body.Left + 4, body.Top + 21, 38, 40), inset));
            graphics.FillEllipse(brush, Inset(new Rectangle(body.Right - 42, body.Top + 21, 38, 40), inset));

            const int lobeCount = 6;
            const int lobeWidth = 56;
            double spacing = (body.Width - lobeWidth) / (double)(lobeCount - 1);
            for (int index = 0; index < lobeCount; index++)
            {
                int x = body.Left + (int)Math.Round(index * spacing);
                bool edge = index == 0 || index == lobeCount - 1;
                int topOffset = edge ? 9 : (index % 2 == 0 ? 2 : 0);
                int bottomOffset = edge ? 36 : (index % 2 == 0 ? 39 : 42);
                int bottomHeight = body.Height - bottomOffset;
                graphics.FillEllipse(brush, Inset(new Rectangle(x, body.Top + topOffset, lobeWidth, 43), inset));
                graphics.FillEllipse(brush, Inset(new Rectangle(x, body.Top + bottomOffset, lobeWidth, bottomHeight), inset));
            }
        }

        private void DrawSpeechTail(Graphics graphics, Rectangle body, Brush borderBrush, Brush fillBrush)
        {
            int center = Width / 2;
            Point[] outer;
            Point[] inner;
            if (tailAtTop)
            {
                outer = new[] { new Point(center - 14, body.Top + 2), new Point(center + 10, body.Top + 2), new Point(center + 2, 7) };
                inner = new[] { new Point(center - 9, body.Top + 2), new Point(center + 6, body.Top + 2), new Point(center + 2, 12) };
            }
            else
            {
                outer = new[] { new Point(center - 14, body.Bottom - 2), new Point(center + 10, body.Bottom - 2), new Point(center + 2, Height - 7) };
                inner = new[] { new Point(center - 9, body.Bottom - 2), new Point(center + 6, body.Bottom - 2), new Point(center + 2, Height - 12) };
            }
            graphics.FillPolygon(borderBrush, outer);
            graphics.FillPolygon(fillBrush, inner);
        }

        private void DrawThoughtDots(Graphics graphics, Brush borderBrush, Brush fillBrush)
        {
            int center = Width / 2;
            Rectangle large = tailAtTop ? new Rectangle(center - 5, 15, 15, 15) : new Rectangle(center - 5, 78, 15, 15);
            Rectangle small = tailAtTop ? new Rectangle(center + 8, 3, 9, 9) : new Rectangle(center + 8, 99, 9, 9);
            graphics.FillEllipse(borderBrush, large);
            graphics.FillEllipse(fillBrush, Inset(large, 2));
            graphics.FillEllipse(borderBrush, small);
            graphics.FillEllipse(fillBrush, Inset(small, 2));
        }

        private static Rectangle Inset(Rectangle rectangle, int amount)
        {
            return new Rectangle(rectangle.X + amount, rectangle.Y + amount, Math.Max(1, rectangle.Width - amount * 2), Math.Max(1, rectangle.Height - amount * 2));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) messageFont.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class CodexTaskMonitor
    {
        private const int LifecycleReadChunkBytes = 256 * 1024;
        private const int LifecycleMarkerOverlap = 64;
        private const int CandidateRefreshSeconds = 2;
        private const double RecentSessionHours = 24.0;
        private const string TaskStartedMarker = "\"type\":\"task_started\"";
        private const string TaskCompleteMarker = "\"type\":\"task_complete\"";
        private const string TurnAbortedMarker = "\"type\":\"turn_aborted\"";
        private readonly string sessionsRoot;
        private string[] recentSessionPaths;
        private Dictionary<string, long> knownSessionLengths;
        private Dictionary<string, DateTime> observedChangeUntil;
        private Dictionary<string, SessionLifecycleState> lifecycleStates;
        private DateTime nextCandidateRefreshAt;

        private sealed class SessionLifecycleState
        {
            internal long Length;
            internal bool HasMarker;
            internal bool Running;
        }

        internal CodexTaskMonitor()
        {
            sessionsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            recentSessionPaths = new string[0];
            knownSessionLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            observedChangeUntil = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            lifecycleStates = new Dictionary<string, SessionLifecycleState>(StringComparer.OrdinalIgnoreCase);
            nextCandidateRefreshAt = DateTime.MinValue;
        }

        internal bool IsTaskRunning()
        {
            return IsTaskRunning(false);
        }

        internal bool IsTaskRunning(bool refreshCandidates)
        {
            if (!Directory.Exists(sessionsRoot)) return false;

            DateTime now = DateTime.UtcNow;
            if (refreshCandidates || now >= nextCandidateRefreshAt) RefreshRecentSessionPaths(now);

            foreach (string path in recentSessionPaths)
            {
                try
                {
                    FileInfo file = new FileInfo(path);
                    if (!file.Exists) continue;
                    if (FileShowsRunningTask(path)) return true;
                }
                catch { }
            }
            return false;
        }

        private void RefreshRecentSessionPaths(DateTime now)
        {
            List<string> recentPaths = new List<string>();
            Dictionary<string, long> currentLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pendingFolders = new Stack<string>();
            pendingFolders.Push(sessionsRoot);

            while (pendingFolders.Count > 0)
            {
                string folder = pendingFolders.Pop();
                try
                {
                    foreach (string path in Directory.GetFiles(folder, "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            FileInfo file = new FileInfo(path);
                            long previousLength;
                            bool lengthChanged = knownSessionLengths.TryGetValue(path, out previousLength) && previousLength != file.Length;
                            currentLengths[path] = file.Length;
                            if (lengthChanged) observedChangeUntil[path] = now.AddHours(RecentSessionHours);

                            DateTime changeUntil;
                            bool recentlyObservedChanging = observedChangeUntil.TryGetValue(path, out changeUntil) && changeUntil > now;
                            if ((now - file.LastWriteTimeUtc).TotalHours <= RecentSessionHours || recentlyObservedChanging) recentPaths.Add(path);
                        }
                        catch { }
                    }
                }
                catch { }

                try
                {
                    foreach (string childFolder in Directory.GetDirectories(folder)) pendingFolders.Push(childFolder);
                }
                catch { }
            }

            List<string> expiredPaths = new List<string>();
            foreach (KeyValuePair<string, DateTime> item in observedChangeUntil)
            {
                if (item.Value <= now || !currentLengths.ContainsKey(item.Key)) expiredPaths.Add(item.Key);
            }
            foreach (string path in expiredPaths) observedChangeUntil.Remove(path);

            expiredPaths.Clear();
            foreach (string path in lifecycleStates.Keys)
            {
                if (!currentLengths.ContainsKey(path)) expiredPaths.Add(path);
            }
            foreach (string path in expiredPaths) lifecycleStates.Remove(path);

            knownSessionLengths = currentLengths;
            recentSessionPaths = recentPaths.ToArray();
            nextCandidateRefreshAt = now.AddSeconds(CandidateRefreshSeconds);
        }

        private bool FileShowsRunningTask(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long currentLength = stream.Length;
                    SessionLifecycleState state;
                    if (!lifecycleStates.TryGetValue(path, out state) || currentLength < state.Length)
                    {
                        state = new SessionLifecycleState();
                        bool running;
                        state.HasMarker = TryFindLatestLifecycleFromEnd(stream, out running);
                        state.Running = state.HasMarker && running;
                        state.Length = currentLength;
                        lifecycleStates[path] = state;
                        return state.Running;
                    }

                    if (currentLength > state.Length)
                    {
                        bool running;
                        long readFrom = Math.Max(0, state.Length - LifecycleMarkerOverlap);
                        if (TryReadLatestLifecycle(stream, readFrom, currentLength, out running))
                        {
                            state.HasMarker = true;
                            state.Running = running;
                        }
                        state.Length = currentLength;
                    }

                    return state.HasMarker && state.Running;
                }
            }
            catch { return false; }
        }

        private static bool TryFindLatestLifecycleFromEnd(FileStream stream, out bool running)
        {
            running = false;
            long end = stream.Length;
            while (end > 0)
            {
                long start = Math.Max(0, end - LifecycleReadChunkBytes);
                if (TryReadLatestLifecycle(stream, start, end, out running)) return true;
                if (start == 0) break;
                end = start + LifecycleMarkerOverlap;
            }
            return false;
        }

        private static bool TryReadLatestLifecycle(FileStream stream, long start, long end, out bool running)
        {
            running = false;
            if (end <= start) return false;

            stream.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[LifecycleReadChunkBytes];
            long remaining = end - start;
            string carry = string.Empty;
            bool found = false;

            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read <= 0) break;
                remaining -= read;

                string block = carry + Encoding.UTF8.GetString(buffer, 0, read);
                int started = block.LastIndexOf(TaskStartedMarker, StringComparison.Ordinal);
                int completed = block.LastIndexOf(TaskCompleteMarker, StringComparison.Ordinal);
                int aborted = block.LastIndexOf(TurnAbortedMarker, StringComparison.Ordinal);
                int latestFinished = Math.Max(completed, aborted);
                if (started >= 0 || latestFinished >= 0)
                {
                    found = true;
                    running = started > latestFinished;
                }

                int carryStart = Math.Max(0, block.Length - LifecycleMarkerOverlap);
                carry = block.Substring(carryStart);
            }

            return found;
        }
    }

    internal sealed class PetWalkerContext : ApplicationContext
    {
        private const int InteractionMargin = 40;
        private const int StartupCodexDelaySeconds = 8;
        private const string SettingsRegistryPath = @"Software\CodexPetWalker";
        private readonly NotifyIcon trayIcon;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem stoppedItem;
        private readonly ToolStripMenuItem slowItem;
        private readonly ToolStripMenuItem normalItem;
        private readonly ToolStripMenuItem fastItem;
        private readonly ToolStripMenuItem pretendWorkItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Random random;
        private readonly AnimatedPetWindow pet;
        private readonly TaskBubbleWindow taskBubble;
        private readonly CodexTaskMonitor taskMonitor;
        private readonly bool launchCodexAtStartup;
        private readonly DateTime startupCodexLaunchAt;

        private IntPtr nativePetWindow;
        private bool walking;
        private WalkingSpeed speed;
        private DateTime nextActionAt;
        private DateTime nextIdleGestureAt;
        private DateTime lastSearchAt;
        private DateTime lastTaskCheckAt;
        private DateTime activateCodexUntil;
        private string codexExecutablePath;
        private DateTime walkStartedAt;
        private bool taskStateInitialized;
        private bool codexTaskRunning;
        private bool pretendTaskRunning;
        private bool startupCodexLaunchAttempted;
        private bool completedTaskAcknowledged;
        private bool completedTaskRequiresReview;
        private TaskBubblePhase taskBubblePhase;
        private DateTime taskBubblePhaseEndsAt;
        private double walkDurationSeconds;
        private int startX;
        private int startY;
        private int targetX;
        private int targetY;
        private int expectedX;
        private int expectedY;

        internal bool RestartRequested { get; private set; }

        internal PetWalkerContext(bool launchCodexAtStartup)
        {
            string spriteSheetPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "spritesheet.png");
            if (!System.IO.File.Exists(spriteSheetPath)) throw new System.IO.FileNotFoundException("spritesheet.png 파일을 실행 파일과 같은 폴더에서 찾지 못했습니다.", spriteSheetPath);

            random = new Random();
            taskMonitor = new CodexTaskMonitor();
            this.launchCodexAtStartup = launchCodexAtStartup;
            startupCodexLaunchAt = DateTime.UtcNow.AddSeconds(StartupCodexDelaySeconds);
            codexExecutablePath = LoadCodexExecutablePath();
            speed = WalkingSpeed.Normal;
            nextActionAt = DateTime.UtcNow.AddSeconds(2);
            nextIdleGestureAt = DateTime.UtcNow.AddSeconds(RandomBetween(12.0, 24.0));
            lastSearchAt = DateTime.MinValue;
            lastTaskCheckAt = DateTime.MinValue;
            activateCodexUntil = DateTime.MinValue;

            statusItem = new ToolStripMenuItem("펫 찾는 중...");
            statusItem.Enabled = false;

            stoppedItem = new ToolStripMenuItem("정지");
            slowItem = new ToolStripMenuItem("느리게");
            normalItem = new ToolStripMenuItem("보통");
            fastItem = new ToolStripMenuItem("빠르게");
            stoppedItem.Click += delegate { SetSpeed(WalkingSpeed.Stopped); };
            slowItem.Click += delegate { SetSpeed(WalkingSpeed.Slow); };
            normalItem.Click += delegate { SetSpeed(WalkingSpeed.Normal); };
            fastItem.Click += delegate { SetSpeed(WalkingSpeed.Fast); };

            ToolStripMenuItem speedMenu = new ToolStripMenuItem("산책 속도");
            speedMenu.DropDownItems.Add(stoppedItem);
            speedMenu.DropDownItems.Add(new ToolStripSeparator());
            speedMenu.DropDownItems.Add(slowItem);
            speedMenu.DropDownItems.Add(normalItem);
            speedMenu.DropDownItems.Add(fastItem);

            pretendWorkItem = new ToolStripMenuItem("작업하는 척 하기");
            pretendWorkItem.Click += delegate { StartPretendWork(); };

            startupItem = new ToolStripMenuItem("Windows 시작 시 Codex와 함께 자동 실행");
            startupItem.CheckOnClick = true;
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += ToggleStartup;
            if (startupItem.Checked) UpgradeStartupRegistration();

            ToolStripMenuItem reconnectItem = new ToolStripMenuItem("원본 펫 다시 연결");
            reconnectItem.Click += delegate { RestoreNativePet(); nativePetWindow = IntPtr.Zero; FindAndReplaceNativePet(); };
            ToolStripMenuItem restartItem = new ToolStripMenuItem("산책 도우미 재실행");
            restartItem.Click += delegate { RestartWalker(); };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("산책 도우미 종료");
            exitItem.Click += delegate { ExitWalker(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(speedMenu);
            menu.Items.Add(pretendWorkItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(reconnectItem);
            menu.Items.Add(restartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            trayIcon = new NotifyIcon();
            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "Codex 펫 산책 도우미";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;

            pet = new AnimatedPetWindow(spriteSheetPath);
            pet.ContextMenuStrip = menu;
            pet.DoubleClick += delegate { ExitWalker(); };
            pet.PetClicked += HandlePetClicked;
            pet.PetDragged += HandlePetDragged;
            taskBubble = new TaskBubbleWindow();
            pet.LocationChanged += delegate { taskBubble.PositionNear(pet); };
            SetSpeed(WalkingSpeed.Normal);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 33;
            timer.Tick += OnTimerTick;
            timer.Start();

            FindAndReplaceNativePet();
            trayIcon.ShowBalloonTip(2500, "Codex 펫 산책 도우미", "Codex 작업 상태와 산책·인사·클릭 반응을 자연스럽게 재생합니다.", ToolTipIcon.Info);
        }

        private void OnTimerTick(object sender, EventArgs eventArgs)
        {
            DateTime now = DateTime.UtcNow;
            TryLaunchCodexAtStartup(now);
            if (!NativeMethods.IsWindow(nativePetWindow))
            {
                nativePetWindow = IntPtr.Zero;
                walking = false;
                pet.Hide();
                taskBubble.Hide();
                statusItem.Text = "원본 펫 찾는 중...";
                if ((now - lastSearchAt).TotalSeconds >= 2) FindAndReplaceNativePet();
                return;
            }

            if (NativeMethods.IsWindowVisible(nativePetWindow)) NativeMethods.ShowWindow(nativePetWindow, NativeMethods.SwHide);
            TryActivateCodexWindow(now);
            pet.UpdateAnimation(now);
            UpdateCodexTaskState(now);
            UpdateTaskBubble(now);

            if (pet.IsBeingDragged)
            {
                walking = false;
                expectedX = pet.Left;
                expectedY = pet.Top;
                SetMotionStatus();
                return;
            }

            if (codexTaskRunning || pretendTaskRunning)
            {
                walking = false;
                pet.SetAnimation(PetAnimation.TaskWorking, now);
                expectedX = pet.Left;
                expectedY = pet.Top;
                SetMotionStatus();
                return;
            }

            if (IsUserInteractingNearPet() || Math.Abs(pet.Left - expectedX) > 3 || Math.Abs(pet.Top - expectedY) > 3)
            {
                PauseAfterInteraction(now);
                return;
            }

            if (!walking)
            {
                if (pet.IsTransientAnimation) return;
                pet.SetAnimation(PetAnimation.Idle, now);
                if (now >= nextIdleGestureAt)
                {
                    PlayIdleGesture(now);
                    return;
                }
                if (speed == WalkingSpeed.Stopped)
                {
                    SetMotionStatus();
                    return;
                }
                if (now >= nextActionAt) StartWalk(now);
                else SetMotionStatus();
                return;
            }

            double progress = Math.Min(1.0, (now - walkStartedAt).TotalSeconds / walkDurationSeconds);
            double eased = progress * progress * (3.0 - 2.0 * progress);
            int x = startX + (int)Math.Round((targetX - startX) * eased);
            int y = startY + (int)Math.Round((targetY - startY) * eased);
            pet.MovePet(x, y);
            expectedX = x;
            expectedY = y;

            if (progress >= 1.0)
            {
                walking = false;
                pet.SetAnimation(PetAnimation.Idle, now);
                SetMotionStatus();
                nextActionAt = now.AddSeconds(RandomBetween(7.0, 18.0));
                if (random.NextDouble() < 0.18)
                {
                    pet.PlayTransient(PetAnimation.Waving, now, 1);
                    SetMotionStatus();
                }
            }
        }

        private void UpdateCodexTaskState(DateTime now)
        {
            if ((now - lastTaskCheckAt).TotalMilliseconds < 750) return;
            lastTaskCheckAt = now;
            bool running = taskMonitor.IsTaskRunning();

            if (!taskStateInitialized)
            {
                taskStateInitialized = true;
                codexTaskRunning = running;
                if (running)
                {
                    pretendTaskRunning = false;
                    walking = false;
                    pet.SetAnimation(PetAnimation.TaskWorking, now);
                    ShowWorkingThoughtBubble();
                    UpdatePretendWorkMenu();
                }
                return;
            }

            if (running == codexTaskRunning) return;
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;

            if (running)
            {
                BeginDetectedRealTask(now);
            }
            else
            {
                codexTaskRunning = false;
                pet.PlayTransient(PetAnimation.Jumping, now, 1);
                nextActionAt = now.AddSeconds(RandomBetween(9.0, 16.0));
                nextIdleGestureAt = now.AddSeconds(RandomBetween(18.0, 35.0));
                ShowTaskCompletedBubble(now, true);
                SetMotionStatus();
            }
        }

        private void BeginDetectedRealTask(DateTime now)
        {
            taskStateInitialized = true;
            codexTaskRunning = true;
            pretendTaskRunning = false;
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;
            pet.SetAnimation(PetAnimation.TaskWorking, now);
            ShowTaskStartedBubble(now);
            UpdatePretendWorkMenu();
            SetMotionStatus();
        }

        private void ShowTaskStartedBubble(DateTime now)
        {
            completedTaskAcknowledged = false;
            taskBubblePhase = TaskBubblePhase.Starting;
            taskBubblePhaseEndsAt = now.AddSeconds(3);
            taskBubble.ShowMessage("작업을 시작할게요!", BubbleStyle.Speech, pet);
        }

        private void ShowWorkingThoughtBubble()
        {
            taskBubblePhase = TaskBubblePhase.Working;
            taskBubblePhaseEndsAt = DateTime.MaxValue;
            taskBubble.ShowMessage("열심히 작업 중이에요...", BubbleStyle.Thought, pet);
        }

        private void ShowTaskCompletedBubble(DateTime now, bool requiresReview)
        {
            completedTaskAcknowledged = false;
            completedTaskRequiresReview = requiresReview;
            taskBubblePhase = TaskBubblePhase.Completed;
            taskBubblePhaseEndsAt = now.AddSeconds(3);
            taskBubble.ShowMessage("작업을 마쳤어요!", BubbleStyle.Speech, pet);
            UpdatePretendWorkMenu();
        }

        private void UpdateTaskBubble(DateTime now)
        {
            if (taskBubblePhase == TaskBubblePhase.Hidden) return;

            if (taskBubblePhase == TaskBubblePhase.Starting)
            {
                if (now >= taskBubblePhaseEndsAt)
                {
                    if (codexTaskRunning || pretendTaskRunning) ShowWorkingThoughtBubble();
                    else taskBubble.Hide();
                }
                return;
            }

            if (taskBubblePhase == TaskBubblePhase.Working)
            {
                if (!taskBubble.Visible) taskBubble.ShowMessage("열심히 작업 중이에요...", BubbleStyle.Thought, pet);
                return;
            }

            if (taskBubblePhase == TaskBubblePhase.Completed)
            {
                if (completedTaskRequiresReview && IsCodexWindowForeground()) completedTaskAcknowledged = true;
                if (now < taskBubblePhaseEndsAt) return;
                if (completedTaskAcknowledged || !completedTaskRequiresReview)
                {
                    taskBubblePhase = TaskBubblePhase.Hidden;
                    taskBubble.Hide();
                    UpdatePretendWorkMenu();
                    SetMotionStatus();
                }
                else
                {
                    taskBubblePhase = TaskBubblePhase.AwaitingReview;
                    taskBubble.ShowMessage("결과를 확인해 주셨으면...", BubbleStyle.Thought, pet);
                }
                return;
            }

            if (taskBubblePhase == TaskBubblePhase.AwaitingReview)
            {
                if (IsCodexWindowForeground())
                {
                    completedTaskAcknowledged = true;
                    taskBubblePhase = TaskBubblePhase.Hidden;
                    taskBubble.Hide();
                    UpdatePretendWorkMenu();
                    SetMotionStatus();
                }
                else if (!taskBubble.Visible)
                {
                    taskBubble.ShowMessage("결과를 확인해 주셨으면...", BubbleStyle.Thought, pet);
                }
            }
        }

        private bool IsCodexWindowForeground()
        {
            IntPtr mainWindow = FindCodexMainWindow();
            return mainWindow != IntPtr.Zero && NativeMethods.GetForegroundWindow() == mainWindow;
        }

        private void AcknowledgeCompletedTask()
        {
            if (taskBubblePhase != TaskBubblePhase.Completed && taskBubblePhase != TaskBubblePhase.AwaitingReview) return;
            completedTaskAcknowledged = true;
            if (taskBubblePhase == TaskBubblePhase.AwaitingReview)
            {
                taskBubblePhase = TaskBubblePhase.Hidden;
                taskBubble.Hide();
                UpdatePretendWorkMenu();
                SetMotionStatus();
            }
        }

        private void StartPretendWork()
        {
            if (codexTaskRunning || pretendTaskRunning || taskBubblePhase == TaskBubblePhase.Completed || taskBubblePhase == TaskBubblePhase.AwaitingReview) return;

            DateTime now = DateTime.UtcNow;
            if (taskMonitor.IsTaskRunning(true))
            {
                BeginDetectedRealTask(now);
                return;
            }

            pretendTaskRunning = true;
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;
            pet.SetAnimation(PetAnimation.TaskWorking, now);
            ShowTaskStartedBubble(now);
            UpdatePretendWorkMenu();
            SetMotionStatus();
        }

        private void FinishPretendWork()
        {
            if (!pretendTaskRunning) return;

            DateTime now = DateTime.UtcNow;
            pretendTaskRunning = false;
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;
            pet.PlayTransient(PetAnimation.Jumping, now, 1);
            nextActionAt = now.AddSeconds(RandomBetween(9.0, 16.0));
            nextIdleGestureAt = now.AddSeconds(RandomBetween(18.0, 35.0));
            ShowTaskCompletedBubble(now, false);
            SetMotionStatus();
        }

        private void UpdatePretendWorkMenu()
        {
            bool completionVisible = taskBubblePhase == TaskBubblePhase.Completed || taskBubblePhase == TaskBubblePhase.AwaitingReview;
            pretendWorkItem.Enabled = !codexTaskRunning && !pretendTaskRunning && !completionVisible;
        }

        private void PlayIdleGesture(DateTime now)
        {
            walking = false;
            double gesture = random.NextDouble();
            if (gesture < 0.45)
            {
                pet.PlayTransient(PetAnimation.Waving, now, random.Next(1, 3));
                SetMotionStatus();
            }
            else if (gesture < 0.80)
            {
                pet.PlayTransient(PetAnimation.RunningGesture, now, 2);
                SetMotionStatus();
            }
            else
            {
                pet.PlayTransient(PetAnimation.Jumping, now, 1);
                SetMotionStatus();
            }
            nextActionAt = now.AddSeconds(RandomBetween(4.0, 7.0));
            nextIdleGestureAt = now.AddSeconds(RandomBetween(20.0, 45.0));
        }

        private void FindAndReplaceNativePet()
        {
            lastSearchAt = DateTime.UtcNow;
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (IsNativePetOverlay(window)) { found = window; return false; }
                return true;
            }, IntPtr.Zero);

            if (found == IntPtr.Zero) { statusItem.Text = "원본 펫 찾는 중..."; return; }
            nativePetWindow = found;
            uint nativeProcessId;
            NativeMethods.GetWindowThreadProcessId(nativePetWindow, out nativeProcessId);
            try
            {
                string discoveredPath = Process.GetProcessById((int)nativeProcessId).MainModule.FileName;
                if (!string.IsNullOrEmpty(discoveredPath))
                {
                    codexExecutablePath = discoveredPath;
                    SaveCodexExecutablePath(discoveredPath);
                }
            }
            catch { }
            NativeRect rect;
            if (!NativeMethods.GetWindowRect(nativePetWindow, out rect)) { nativePetWindow = IntPtr.Zero; return; }

            int petX = rect.Left + (rect.Width - AnimatedPetWindow.PetWidth) / 2;
            int petY = rect.Top;
            pet.MovePet(petX, petY);
            expectedX = petX;
            expectedY = petY;
            NativeMethods.ShowWindow(nativePetWindow, NativeMethods.SwHide);
            pet.Show();
            DateTime now = DateTime.UtcNow;
            pet.PlayTransient(PetAnimation.Waving, now, 2);
            pet.UpdateAnimation(now);
            walking = false;
            nextActionAt = now.AddSeconds(RandomBetween(8.0, 15.0));
            nextIdleGestureAt = now.AddSeconds(RandomBetween(18.0, 35.0));
            SetMotionStatus();
        }

        private static bool IsNativePetOverlay(IntPtr window)
        {
            if (!NativeMethods.IsWindowVisible(window)) return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            try
            {
                Process process = Process.GetProcessById((int)processId);
                if (!string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { return false; }

            StringBuilder className = new StringBuilder(128);
            StringBuilder title = new StringBuilder(128);
            NativeMethods.GetClassName(window, className, className.Capacity);
            NativeMethods.GetWindowText(window, title, title.Capacity);
            string windowTitle = title.ToString();
            bool knownTitle = string.Equals(windowTitle, "Codex", StringComparison.Ordinal) ||
                              string.Equals(windowTitle, "ChatGPT", StringComparison.Ordinal);
            if (className.ToString() != "Chrome_WidgetWin_1" || !knownTitle) return false;

            long style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64();
            long required = NativeMethods.WsExTopmost | NativeMethods.WsExToolWindow | NativeMethods.WsExLayered;
            return (style & required) == required;
        }

        private bool IsUserInteractingNearPet()
        {
            bool mouseButtonDown = NativeMethods.GetAsyncKeyState(0x01) < 0 || NativeMethods.GetAsyncKeyState(0x02) < 0 || NativeMethods.GetAsyncKeyState(0x04) < 0;
            if (!mouseButtonDown) return false;

            Rectangle interactionArea = pet.Bounds;
            interactionArea.Inflate(InteractionMargin, InteractionMargin);
            return interactionArea.Contains(Cursor.Position);
        }

        private void PauseAfterInteraction(DateTime now)
        {
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;
            pet.SetAnimation(PetAnimation.Idle, now);
            nextActionAt = now.AddSeconds(5);
            SetMotionStatus();
        }

        private void HandlePetClicked(object sender, EventArgs eventArgs)
        {
            if (pretendTaskRunning)
            {
                DateTime pretendClickAt = DateTime.UtcNow;
                if (taskMonitor.IsTaskRunning(true))
                {
                    BeginDetectedRealTask(pretendClickAt);
                    return;
                }
                FinishPretendWork();
                return;
            }

            AcknowledgeCompletedTask();
            OpenCodexWindow();
            if (codexTaskRunning) return;
            DateTime now = DateTime.UtcNow;
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;
            pet.PlayTransient(PetAnimation.Jumping, now, 2);
            nextActionAt = now.AddSeconds(RandomBetween(6.0, 9.0));
            nextIdleGestureAt = now.AddSeconds(RandomBetween(18.0, 35.0));
            SetMotionStatus();
        }

        private void OpenCodexWindow()
        {
            if (string.IsNullOrEmpty(codexExecutablePath)) return;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = codexExecutablePath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
                activateCodexUntil = DateTime.UtcNow.AddSeconds(2);
            }
            catch { }
        }

        private void TryActivateCodexWindow(DateTime now)
        {
            if (now > activateCodexUntil) return;
            IntPtr mainWindow = FindCodexMainWindow();
            if (mainWindow == IntPtr.Zero || !NativeMethods.IsWindowVisible(mainWindow)) return;
            NativeMethods.SetForegroundWindow(mainWindow);
            activateCodexUntil = DateTime.MinValue;
        }

        private IntPtr FindCodexMainWindow()
        {
            uint codexProcessId;
            NativeMethods.GetWindowThreadProcessId(nativePetWindow, out codexProcessId);
            IntPtr mainWindow = IntPtr.Zero;
            long largestArea = 0;

            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint processId;
                NativeMethods.GetWindowThreadProcessId(window, out processId);
                if (processId != codexProcessId || window == nativePetWindow) return true;

                StringBuilder className = new StringBuilder(128);
                NativeMethods.GetClassName(window, className, className.Capacity);
                if (className.ToString() != "Chrome_WidgetWin_1") return true;

                long style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlExStyle).ToInt64();
                if ((style & NativeMethods.WsExToolWindow) != 0) return true;

                NativeRect rect;
                if (!NativeMethods.GetWindowRect(window, out rect)) return true;
                long area = (long)Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
                if (area > largestArea)
                {
                    largestArea = area;
                    mainWindow = window;
                }
                return true;
            }, IntPtr.Zero);
            return mainWindow;
        }

        private void HandlePetDragged(object sender, EventArgs eventArgs)
        {
            DateTime now = DateTime.UtcNow;
            walking = false;
            expectedX = pet.Left;
            expectedY = pet.Top;
            if (!codexTaskRunning) pet.PlayTransient(PetAnimation.Waving, now, 1);
            nextActionAt = now.AddSeconds(6);
            nextIdleGestureAt = now.AddSeconds(RandomBetween(18.0, 35.0));
            SetMotionStatus();
        }

        private void StartWalk(DateTime now)
        {
            if (speed == WalkingSpeed.Stopped)
            {
                walking = false;
                SetMotionStatus();
                return;
            }

            Rectangle area = Screen.FromControl(pet).WorkingArea;
            int minX = area.Left;
            int minY = area.Top;
            int maxX = Math.Max(minX, area.Right - AnimatedPetWindow.PetWidth);
            int maxY = Math.Max(minY, area.Bottom - AnimatedPetWindow.PetHeight);
            int chosenX = pet.Left;
            int chosenY = pet.Top;
            double distance = 0;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                chosenX = random.Next(minX, maxX + 1);
                chosenY = random.Next(minY, maxY + 1);
                distance = Distance(pet.Left, pet.Top, chosenX, chosenY);
                if (distance >= 140) break;
            }
            if (distance < 20) { nextActionAt = now.AddSeconds(3); return; }

            startX = pet.Left;
            startY = pet.Top;
            targetX = chosenX;
            targetY = chosenY;
            expectedX = pet.Left;
            expectedY = pet.Top;
            walkStartedAt = now;
            walkDurationSeconds = Math.Max(1.0, distance / PixelsPerSecond());
            walking = true;
            pet.SetAnimation(targetX < startX ? PetAnimation.RunningLeft : PetAnimation.RunningRight, now);
            SetMotionStatus();
        }

        private double PixelsPerSecond()
        {
            switch (speed)
            {
                case WalkingSpeed.Slow: return 45.0;
                case WalkingSpeed.Fast: return 125.0;
                default: return 80.0;
            }
        }

        private static double Distance(int x1, int y1, int x2, int y2)
        {
            int deltaX = x2 - x1;
            int deltaY = y2 - y1;
            return Math.Sqrt((double)deltaX * deltaX + (double)deltaY * deltaY);
        }

        private double RandomBetween(double minimum, double maximum) { return minimum + random.NextDouble() * (maximum - minimum); }

        private void SetSpeed(WalkingSpeed newSpeed)
        {
            bool wasStopped = speed == WalkingSpeed.Stopped;
            speed = newSpeed;
            stoppedItem.Checked = speed == WalkingSpeed.Stopped;
            slowItem.Checked = speed == WalkingSpeed.Slow;
            normalItem.Checked = speed == WalkingSpeed.Normal;
            fastItem.Checked = speed == WalkingSpeed.Fast;

            DateTime now = DateTime.UtcNow;
            if (speed == WalkingSpeed.Stopped)
            {
                walking = false;
                expectedX = pet.Left;
                expectedY = pet.Top;
                pet.SetAnimation(PetAnimation.Idle, now);
                SetMotionStatus();
            }
            else if (wasStopped)
            {
                nextActionAt = now.AddSeconds(2);
                SetMotionStatus();
            }
        }

        private void SetMotionStatus()
        {
            if (pretendTaskRunning)
            {
                statusItem.Text = "Codex 작업하는 척 중";
                return;
            }

            if (codexTaskRunning)
            {
                statusItem.Text = "Codex 작업 중";
                return;
            }

            if (completedTaskRequiresReview && (taskBubblePhase == TaskBubblePhase.Completed || taskBubblePhase == TaskBubblePhase.AwaitingReview))
            {
                statusItem.Text = "Codex 작업 완료";
                return;
            }

            switch (pet.CurrentAnimation)
            {
                case PetAnimation.RunningLeft:
                    statusItem.Text = "왼쪽으로 이동 중";
                    break;
                case PetAnimation.RunningRight:
                    statusItem.Text = "오른쪽으로 이동 중";
                    break;
                case PetAnimation.Waving:
                    statusItem.Text = "손 흔드는 중";
                    break;
                case PetAnimation.Jumping:
                    statusItem.Text = "기분 좋게 뛰는 중";
                    break;
                case PetAnimation.RunningGesture:
                    statusItem.Text = "대기 중";
                    break;
                case PetAnimation.TaskWorking:
                    statusItem.Text = "Codex 작업 중";
                    break;
                default:
                    statusItem.Text = speed == WalkingSpeed.Stopped ? "산책 속도 · 정지" : "대기 중";
                    break;
            }
        }

        private void TryLaunchCodexAtStartup(DateTime now)
        {
            if (!launchCodexAtStartup || startupCodexLaunchAttempted || now < startupCodexLaunchAt) return;
            startupCodexLaunchAttempted = true;
            if (Process.GetProcessesByName("ChatGPT").Length > 0) return;

            if (string.IsNullOrEmpty(codexExecutablePath))
            {
                trayIcon.ShowBalloonTip(4000, "Codex 자동 실행 실패", "Codex 실행 경로를 찾지 못했습니다. Codex를 한 번 직접 실행해 주세요.", ToolTipIcon.Warning);
                return;
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = codexExecutablePath;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                trayIcon.ShowBalloonTip(5000, "Codex 자동 실행 실패", exception.Message, ToolTipIcon.Warning);
            }
        }

        private static string LoadCodexExecutablePath()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsRegistryPath, false))
                {
                    return key == null ? null : key.GetValue("CodexExecutablePath") as string;
                }
            }
            catch { return null; }
        }

        private static void SaveCodexExecutablePath(string path)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath))
                {
                    key.SetValue("CodexExecutablePath", path, RegistryValueKind.String);
                }
            }
            catch { }
        }

        private static string StartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\" --startup";
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    string value = key.GetValue("CodexPetWalker") as string;
                    string previousCommand = "\"" + Application.ExecutablePath + "\"";
                    return string.Equals(value, StartupCommand(), StringComparison.OrdinalIgnoreCase) || string.Equals(value, previousCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private void ToggleStartup(object sender, EventArgs eventArgs)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (startupItem.Checked)
                    {
                        if (!string.IsNullOrEmpty(codexExecutablePath)) SaveCodexExecutablePath(codexExecutablePath);
                        key.SetValue("CodexPetWalker", StartupCommand(), RegistryValueKind.String);
                    }
                    else key.DeleteValue("CodexPetWalker", false);
                }
            }
            catch (Exception exception)
            {
                startupItem.Checked = !startupItem.Checked;
                MessageBox.Show("자동 실행 설정을 변경하지 못했습니다.\n\n" + exception.Message, "Codex 펫 산책 도우미", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void UpgradeStartupRegistration()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    key.SetValue("CodexPetWalker", StartupCommand(), RegistryValueKind.String);
                }
            }
            catch { }
        }

        private void RestoreNativePet()
        {
            if (!NativeMethods.IsWindow(nativePetWindow)) return;
            NativeRect rect;
            if (NativeMethods.GetWindowRect(nativePetWindow, out rect))
            {
                int overlayX = pet.Left - (rect.Width - AnimatedPetWindow.PetWidth) / 2;
                int overlayY = pet.Top;
                uint flags = NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpNoOwnerZOrder;
                NativeMethods.SetWindowPos(nativePetWindow, IntPtr.Zero, overlayX, overlayY, 0, 0, flags);
            }
            NativeMethods.ShowWindow(nativePetWindow, NativeMethods.SwShowNoActivate);
        }

        private void ExitWalker()
        {
            timer.Stop();
            taskBubble.Hide();
            pet.Hide();
            RestoreNativePet();
            trayIcon.Visible = false;
            ExitThread();
        }

        private void RestartWalker()
        {
            RestartRequested = true;
            ExitWalker();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RestoreNativePet();
                timer.Dispose();
                taskBubble.Dispose();
                pet.Dispose();
                trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        private static Mutex singleInstance;

        [STAThread]
        private static void Main()
        {
            bool launchCodexAtStartup = false;
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase)) launchCodexAtStartup = true;
            }

            bool createdNew;
            singleInstance = new Mutex(true, "CodexPetWalker.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Codex 펫 산책 도우미가 이미 실행 중입니다.", "Codex 펫 산책 도우미", MessageBoxButtons.OK, MessageBoxIcon.Information);
                singleInstance.Dispose();
                return;
            }

            bool restartRequested = false;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                PetWalkerContext context = new PetWalkerContext(launchCodexAtStartup);
                Application.Run(context);
                restartRequested = context.RestartRequested;
            }
            catch (Exception exception)
            {
                MessageBox.Show("산책 도우미를 시작하지 못했습니다.\n\n" + exception.Message, "Codex 펫 산책 도우미", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                singleInstance.ReleaseMutex();
                singleInstance.Dispose();
                singleInstance = null;
            }

            if (!restartRequested) return;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = Application.ExecutablePath;
                startInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (launchCodexAtStartup) startInfo.Arguments = "--startup";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                MessageBox.Show("산책 도우미를 다시 실행하지 못했습니다.\n\n" + exception.Message, "Codex 펫 산책 도우미", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
