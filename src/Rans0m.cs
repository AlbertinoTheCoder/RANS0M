using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Rans0m : Form {
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
    bool preciseTimer;
    const double FrameDuration=1.0/60.0;
    const double RareCoinChance=0.02;
    const double IntroFaceDuration=.18;
    bool introSoundStarted;
    const double AttackFaceDuration=.8;
    const double AttackSlashTime=2.0;
    double attackStarted, introEnds, timeOffset;
    double? spawnClockOverride;
    readonly Dictionary<string,double> soundStarts=new Dictionary<string,double>();
    const int MaxCoins=7;
    const int MaxGlitchCoins=2;
    readonly List<bool> coinRespawnKinds=new List<bool>();
    const int MaxPopups=48;
    readonly List<Rectangle> respawnZones=new List<Rectangle>();
    double nextPopupAt, nextReplacementAt, nextBurstAt, nextTheftAt;
    readonly List<CursorTrace> cursorTrail=new List<CursorTrace>();
    double? popupExpiryOverride;
    int popupMonitorIndex;
    readonly List<double> coinRespawns=new List<double>();
    double nextFrame, previousFrame;
    bool resourcesReleased, yWasDown, handVisible;
    double lastWorldGlitch, lastCoinGlitch;
    int lastCountdown=-1,lastShownBalance=-1,lastShownCombo=-1;
    Point previousPointer;
    int openingPopups;
    double openingPopupAt;

    // Multimedia wakeups avoid the WinForms timer's ~10-16 ms minimum tick.
    // Only one UI callback can be queued, so slow frames never build a backlog.
    sealed class FrameTimer : IDisposable {
        public const int Message=0x8000+172;
        delegate void TimerCallback(uint id,uint message,UIntPtr user,UIntPtr first,UIntPtr second);
        [DllImport("winmm.dll")] static extern uint timeSetEvent(uint delay,uint resolution,TimerCallback callback,UIntPtr user,uint mode);
        [DllImport("winmm.dll")] static extern uint timeKillEvent(uint id);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr first,IntPtr second);
        readonly Control owner;
        readonly TimerCallback callback;
        IntPtr window;
        uint id;
        int queued;
        volatile int generation, running;
        bool disposed;
        public int Interval=1;
        public event EventHandler Tick;
        public FrameTimer(Control control){owner=control;callback=Wake;}
        void Wake(uint eventId,uint message,UIntPtr user,UIntPtr first,UIntPtr second){
            if(running==0||System.Threading.Interlocked.CompareExchange(ref queued,1,0)!=0)return;
            if(!PostMessage(window,Message,new IntPtr(generation),IntPtr.Zero))System.Threading.Interlocked.Exchange(ref queued,0);
        }
        public void Dispatch(int token){
            if(token!=generation)return;
            System.Threading.Interlocked.Exchange(ref queued,0);
            if(running!=0&&!owner.IsDisposed&&Tick!=null)Tick(this,EventArgs.Empty);
        }
        public void Start(){
            if(disposed)throw new ObjectDisposedException("FrameTimer");
            if(id!=0)return;
            window=owner.Handle;generation++;running=1;
            id=timeSetEvent((uint)Math.Max(1,Interval),1,callback,UIntPtr.Zero,0x101);
            if(id==0){running=0;throw new InvalidOperationException("Could not start frame timer.");}
        }
        public void Stop(){
            running=0;generation++;
            uint active=id;id=0;if(active!=0)timeKillEvent(active);
            System.Threading.Interlocked.Exchange(ref queued,0);
        }
        public void Dispose(){if(!disposed){Stop();disposed=true;}}
    }
    sealed class MonitorTint : Form {
        public MonitorTint(Rectangle bounds){
            FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
            Bounds=bounds;BackColor=Color.FromArgb(180,0,0);Opacity=.18;
            ShowInTaskbar=false;TopMost=true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {CreateParams p=base.CreateParams;p.ExStyle|=0x20|0x80|0x08000000;return p;}
        }
        protected override void WndProc(ref Message m){
            base.WndProc(ref m);if(m.Msg==0x84)m.Result=new IntPtr(-1);
        }
    }
    sealed class CorruptionWave : Form {
        public CorruptionWave(){
            FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
            ShowInTaskbar=false;TopMost=true;DoubleBuffered=true;BackColor=Color.Red;Opacity=.32;
        }
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=0x20|0x80|0x08000000;return p;}}
        protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0x84)m.Result=new IntPtr(-1);}
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);
            using(LinearGradientBrush beam=new LinearGradientBrush(ClientRectangle,Color.DarkRed,Color.Red,0f))e.Graphics.FillRectangle(beam,ClientRectangle);
            e.Graphics.FillRectangle(Brushes.Salmon,Width/2-1,0,3,Height);
        }
    }
    sealed class FailureView : Form {
        readonly Rans0m game;
        public FailureView(Rans0m owner){
            game=owner;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
            ShowInTaskbar=false;TopMost=true;DoubleBuffered=true;
            BackColor=Color.FromArgb(1,2,3);TransparencyKey=BackColor;
            Location=owner.PointToScreen(new Point(owner.ClientSize.Width/2-238,owner.ClientSize.Height/2-238));
            Size=new Size(476,476);
        }
        public void Prepare(){CreateHandle();}
        public void Place(){Location=game.PointToScreen(new Point(game.ClientSize.Width/2-238,game.ClientSize.Height/2-238));}
        protected override bool ShowWithoutActivation {get{return true;}}
        protected override CreateParams CreateParams {get{CreateParams p=base.CreateParams;p.ExStyle|=0x80|0x08000000;return p;}}
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);double age=game.Now()-game.phaseStarted;
            if(age>=game.SoundDuration("failure"))return;
            Point offset=JumpscareOffset(age,game.SoundDuration("failure"),game.reducedMotion);
            if(game.jumpscare!=null)e.Graphics.DrawImage(game.jumpscare,new Rectangle(18+offset.X,18+offset.Y,440,440));
        }
    }
    sealed class CoinBurst {
        public Point Center;public double Birth;
        public CoinBurst(Point center,double birth){Center=center;Birth=birth;}
    }
    sealed class CursorTrace {
        public Point Position;public double Birth;
        public CursorTrace(Point position,double birth){Position=position;Birth=birth;}
    }
    sealed class Pickup {
        public Point Position;public int Value;public double Birth;public bool Rare;
        public Pickup(Point position,int value,double birth,bool rare){Position=position;Value=value;Birth=birth;Rare=rare;}
    }
    sealed class Gold {
        public Point Position;
        public int Value { get { return Rare?100:(Decoy?0:10); } }
        public double Birth;
        public bool Decoy;
        public bool IsFake { get { return Decoy&&!Rare; } }
        public bool Rare, Moves;
        public Point From;
        public Point? Target;
        public double NextMove, MoveStarted;
        public readonly List<CursorTrace> Trail=new List<CursorTrace>();
        public Gold(Point p, bool rare) { Position=p; Rare=rare; }
    }
    sealed class FakePopup : Form {
        readonly int style;
        readonly Bitmap face;
        readonly int seed;
        static readonly string[] Names={"GOLD","YOURGOLDISYUMMY","R4NS0M","G0LD.exe","RANS0M_404","G1VE_ME_G0LD","PAY_UP","GOLD_NOT_FOUND","R4N$0M","C0RRUPT_GOLD","YOUR_GOLD_IS_MINE","G0LD_LEECH","RANSOM://GOLD","GOLD_OVERFLOW","R4NS0M.sys","G0LD_0xDEAD","N0_M0RE_G0LD","RANSOM_ERROR","G0LD.zip","Y0UR_G0LD"};
        public string PopupTitle {get;private set;}
        static string TitleFor(int index){return Names[(index&int.MaxValue)%Names.Length];}
        readonly Font titleFont=new Font("Segoe UI",8,FontStyle.Bold);
        readonly Font contentFont=new Font("Consolas",10,FontStyle.Bold);
        public Point FinalPosition { get; private set; }
        public readonly double Birth;
        public double Alpha=0;
        public bool Exiting=false;
        public double ExitingAt=0;
        public bool IsToast;
        public double ExpiresAt=double.PositiveInfinity;
        public bool Dodges { get; private set; }
        int dodgesLeft=3;
        bool closeOnLeft;
        uint lastDodgeTick;
        readonly string message;
        readonly Bitmap glitch;
        public double VisualTime, LastTexturePaint, CorruptUntil;
        public bool Corrupted;
        public float MusicPulse;
        public bool ReactsToMusic {get{return style%3==0;}}
        Bitmap foldingImage;
        public Size FoldSize {get;private set;}
        public Point FoldOrigin {get;private set;}
        public bool Dragging;
        Point dragOffset;
        bool wasDragged, closePressed;
        public Point DriftOrigin { get; private set; }
        public readonly bool MayDrift;
        public Rectangle CloseButton { get { return new Rectangle(closeOnLeft?Width-78:Width-23,3,19,17); } }
        public void SetDriftPosition(Point point){FinalPosition=point;}
        public bool Animated { get { return glitch!=null; } }
        public bool Steals { get { return style==9&&!IsToast; } }
        public FakePopup(int index, Rectangle bounds, double birth, Bitmap faceImage, bool toast, string text) {
            message=text;Dodges=!toast&&index%4==2;
            style=index%10; face=faceImage; seed=index*127+13;
            PopupTitle=TitleFor(index);Text=PopupTitle;
            FinalPosition=bounds.Location;DriftOrigin=bounds.Location;MayDrift=!toast&&index%4==0; Birth=birth; IsToast=toast;
            StartPosition=FormStartPosition.Manual;
            Bounds=bounds;
            FormBorderStyle=FormBorderStyle.None;
            ShowInTaskbar=false;
            TopMost=false;
            DoubleBuffered=true;
            BackColor=Color.FromArgb(38,3,5);
            Opacity=0;
            if(style==2||style==5||style==6){
                glitch=new Bitmap(Width+24,Height);
                using(Graphics paint=Graphics.FromImage(glitch)){
                    paint.Clear(Color.Black);
                    Random noise=new Random(seed);
                    for(int i=0;i<190;i++){
                        int y=noise.Next(Math.Max(1,Height-25));
                        Brush color=i%3==0?Brushes.Magenta:(i%3==1?Brushes.Red:Brushes.Black);
                        paint.FillRectangle(color,noise.Next(Width+24),y,noise.Next(3,22),noise.Next(2,10));
                    }
                }
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                CreateParams p=base.CreateParams;
                p.ExStyle|=0x80 | 0x08000000; // toolwindow, no activation; title bar accepts dragging
                return p;
            }
        }
        public void MoveTo(Point position){
            if(Owner==null)return;
            Point screenPoint=Owner.PointToScreen(position);
            Rectangle monitor=Screen.FromRectangle(new Rectangle(screenPoint,Size)).WorkingArea;
            screenPoint=new Point(Math.Max(monitor.Left,Math.Min(monitor.Right-Width,screenPoint.X)),Math.Max(monitor.Top,Math.Min(monitor.Bottom-Height,screenPoint.Y)));
            FinalPosition=Owner.PointToClient(screenPoint);
            wasDragged=true;
            Location=Owner.PointToScreen(FinalPosition);
        }
        protected override void WndProc(ref Message m){
            base.WndProc(ref m);
            if(m.Msg==0x84){
                long packed=m.LParam.ToInt64();
                Point local=PointToClient(new Point((short)(packed&65535),(short)((packed>>16)&65535)));
                if(local.Y>=24)m.Result=new IntPtr(-1); // body stays click-through
            }
        }
        protected override void OnMouseDown(MouseEventArgs e){
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Left&&CloseButton.Contains(e.Location)&&!Exiting){closePressed=true;Capture=true;return;}
            if(e.Button==MouseButtons.Left&&e.Y<24&&!Exiting){wasDragged=true;dragOffset=e.Location;Dragging=true;Capture=true;}
        }
        protected override void OnMouseMove(MouseEventArgs e){
            base.OnMouseMove(e);
            uint tick=unchecked((uint)Environment.TickCount);
            Rans0m game=Owner as Rans0m;
            if(Dodges&&dodgesLeft>0&&!Dragging&&!closePressed&&!Exiting&&(game==null||!game.reducedMotion)&&Rectangle.Inflate(CloseButton,7,5).Contains(e.Location)&&unchecked(tick-lastDodgeTick)>=350){
                Rectangle old=CloseButton;closeOnLeft=!closeOnLeft;dodgesLeft--;lastDodgeTick=tick;
                Invalidate(Rectangle.Union(old,CloseButton));
            }
            if(!Dragging||Owner==null)return;
            Point pointer=Owner.PointToClient(PointToScreen(e.Location));
            MoveTo(new Point(pointer.X-dragOffset.X,pointer.Y-dragOffset.Y));
        }
        protected override void OnMouseUp(MouseEventArgs e){
            base.OnMouseUp(e);
            if(e.Button==MouseButtons.Left){
                bool dismiss=closePressed&&CloseButton.Contains(e.Location);
                closePressed=false;Dragging=false;Capture=false;
                if(dismiss)BeginFold((Owner as Rans0m).Now());
            }
        }
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture){Dragging=false;closePressed=false;}}
        public bool HasBeenDragged { get { return wasDragged; } }
        public void BeginFold(double started){
            if(Exiting||IsDisposed)return;
            Rans0m game=Owner as Rans0m;
            if(game==null||game.reducedMotion){Close();return;}
            foldingImage=new Bitmap(Width,Height);
            DrawToBitmap(foldingImage,new Rectangle(0,0,Width,Height));
            FoldSize=Size;FoldOrigin=FinalPosition;
            Exiting=true;ExitingAt=started;Dragging=false;Capture=false;
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            Graphics g=e.Graphics;
            if(Exiting&&foldingImage!=null){g.DrawImage(foldingImage,ClientRectangle);return;}
            Random random=new Random(seed);
            g.Clear(Color.FromArgb(40+style*13,9,10));
            Color[] fills={Color.FromArgb(238,39,29), Color.FromArgb(14,17,19),Color.FromArgb(120,13,15),Color.FromArgb(7,45,30),Color.FromArgb(132,35,15),Color.FromArgb(18,26,66),Color.FromArgb(223,40,23)};
            using(Brush b=new SolidBrush(fills[style%fills.Length]))g.FillRectangle(b,2,22,Width-4,Height-24);
            RetroTitle(g,new Rectangle(0,0,Width,Height),PopupTitle,CloseButton.Left-10);
            g.DrawRectangle(Pens.Silver,0,0,Width-1,Height-1);
            g.FillRectangle(Brushes.LightGray,CloseButton);g.DrawRectangle(Pens.Gray,CloseButton);
            g.DrawLine(Pens.Black,CloseButton.X+5,7,CloseButton.X+13,15);g.DrawLine(Pens.Black,CloseButton.X+13,7,CloseButton.X+5,15);
            if(IsToast&&message!=null){
                g.DrawString(message,contentFont,Brushes.White,new RectangleF(12,37,Width-24,Height-43));
            } else if(style==7){
                g.FillRectangle(Brushes.Navy,2,25,Width-4,Height-27);
                g.DrawString("FATAL EXCEPTION",contentFont,Brushes.White,9,32);
                g.DrawString("STOP: 0x0000DEAD\nMEMORY_DUMP_FAILED\n\nRetrying...",titleFont,Brushes.White,new RectangleF(9,56,Width-18,Height-60));
            } else if(style==8){
                g.FillRectangle(Brushes.Black,2,25,Width-4,Height-27);
                g.DrawString("MEDIA PLAYER",contentFont,Brushes.Silver,9,32);
                g.DrawString("SIGNAL LOST",contentFont,Brushes.Red,9,64);
                g.DrawLine(Pens.Gray,12,Height-19,Width-12,Height-19);
                Point[] play={new Point(Width-36,34),new Point(Width-36,50),new Point(Width-23,42)};
                g.FillPolygon(Brushes.White,play);
            } else if(Steals){
                g.FillRectangle(Brushes.DarkRed,2,25,Width-4,Height-27);
                g.DrawString("GOLD LEECH",contentFont,Brushes.Yellow,9,31);
                g.DrawString("-5 GOLD EVERY 3s\n\nCLOSE THIS WINDOW",contentFont,Brushes.White,new RectangleF(9,53,Width-18,Height-57));
            } else if(style==0) {
                g.DrawString("YOUR GOLD BELONGS TO US",contentFont,Brushes.White,new RectangleF(8,31,Width-15,48));
                g.DrawString("ERROR 0xC0FFEE",contentFont,Brushes.Black,8,Height-36);
            } else if(style==1) {
                g.DrawString("> ACCESS DENIED\n> RESTORE GOLD? N\n> RANS0M.exe",contentFont,Brushes.Lime,8,31);
            } else if((style==2||style==6)&&face!=null){
                int side=Math.Min(Width-12,Height-29);
                g.FillRectangle(Brushes.Black,2,25,Width-4,Height-27);
                g.DrawImage(face,new Rectangle((Width-side)/2,27,side,side));
            } else if(glitch!=null) {
                System.Drawing.Drawing2D.GraphicsState state=g.Save();
                g.SetClip(new Rectangle(2,25,Width-4,Height-27));
                int shift=(int)(Math.Sin(VisualTime*4+style)*10)-12;
                g.DrawImageUnscaled(glitch,shift,25);
                g.Restore(state);
            } else if(style==3) {
                g.DrawString("DOWNLOADING.....",contentFont,Brushes.White,8,36);
                g.FillRectangle(Brushes.Black,10,70,Width-20,13);
                g.FillRectangle(Brushes.Lime,12,72,Math.Max(5,(Width-24)*((style+3)%9+1)/10),9);
            } else if(style==4) {
                g.DrawString("SYSTEM NOTIFICATION",contentFont,Brushes.White,8,36);
                g.DrawString("RANS0M.exe is downloading...",titleFont,Brushes.White,new RectangleF(8,63,Width-16,35));
            } else {
                g.DrawString("FILE_????????",contentFont,Brushes.White,8,34);
                for(int i=0;i<18;i++) {
                    int x=random.Next(5,Math.Max(6,Width-24)); int y=random.Next(51,Math.Max(52,Height-3));
                    g.FillRectangle((i%3==0)?Brushes.Cyan:Brushes.White,x,y,random.Next(3,18),2);
                }
            }
            if(ReactsToMusic&&MusicPulse>.05f){
                using(Brush pulse=new SolidBrush(Color.FromArgb((int)(MusicPulse*140),255,15,75)))g.FillRectangle(pulse,2,Height-7,Width-4,5);
                if(MusicPulse>.4f)g.FillRectangle(Brushes.Magenta,8,Height/2,(int)((Width-16)*MusicPulse),2);
            }
            if(Corrupted){
                Random scan=new Random(seed+(int)(VisualTime*18));
                for(int i=0;i<7;i++){
                    int y=scan.Next(26,Math.Max(27,Height-4));
                    g.FillRectangle(i%3==0?Brushes.Cyan:Brushes.Red,scan.Next(2,Math.Max(3,Width/3)),y,scan.Next(15,Math.Max(16,Width-10)),2);
                }
            }
            if(Exiting) {
                // Blocky fake pixel corruption while the popup fades away.
                Random r=new Random(style*193+(int)(ExitingAt*100));
                for(int i=0;i<36;i++) {
                    int x=r.Next(0,Math.Max(1,Width)); int y=r.Next(24,Math.Max(25,Height));
                    g.FillRectangle((i%2==0)?Brushes.Black:Brushes.Red,x,y,r.Next(4,25),r.Next(2,11));
                }
            }
        }
        protected override void Dispose(bool disposing) {
            if(disposing){titleFont.Dispose();contentFont.Dispose();if(glitch!=null)glitch.Dispose();if(foldingImage!=null)foldingImage.Dispose();}
            base.Dispose(disposing);
        }
    }

    readonly FrameTimer timer;
    readonly Stopwatch watch=Stopwatch.StartNew();
    readonly Random random=new Random();
    readonly List<Gold> coins=new List<Gold>();
    readonly List<MonitorTint> monitorTints=new List<MonitorTint>();
    readonly List<FakePopup> popups=new List<FakePopup>();
    readonly Dictionary<string,AudioClip> audioClips=new Dictionary<string,AudioClip>();
    readonly Font headline=new Font("Consolas",26,FontStyle.Bold);
    readonly Font small=new Font("Consolas",12,FontStyle.Bold);
    readonly Font caption=new Font("Consolas",9,FontStyle.Bold);
    readonly Font downloadFont=new Font("Consolas",12,FontStyle.Bold);
    FailureView failureView;
    CorruptionWave corruptionWave;
    double nextWaveAt, lastWaveMove, nextTimerGlitchAt, timerGlitchUntil;
    bool timerWasGlitched;
    readonly List<CoinBurst> coinBursts=new List<CoinBurst>();
    double? waveStarted;
    bool stoppedFailureExtras;
    Bitmap face, coinImage, victory, stopSign, jumpscare, popupFace, rareGold, purpleCoin;
    Icon applicationIcon;
    bool muted=false, reducedMotion=false;
    readonly bool desktopMode;
    Point startingMouse;
    Point? ransomPosition;
    bool draggingRansom;
    Point ransomDragOffset;
    readonly List<Pickup> pickups=new List<Pickup>();
    readonly Font uiFont=new Font("Segoe UI",14,FontStyle.Bold);
    readonly Font scoreFont=new Font("Arial",23,FontStyle.Bold);
    readonly Font warningFont=new Font("Arial",21,FontStyle.Bold);
    readonly Font ransomFont=new Font("Arial",12,FontStyle.Bold);
    double displayedBalance;
    bool closeCallWin;
    double secondsAtWin;
    int phase=0, balance=0, combo=0, popupSequence=0;
    double phaseStarted=0, lastCoinAt=-10, lastToast=0;
    double shakeUntil=0, cursorUntil=0;
    readonly Color transparent=Color.FromArgb(1,2,3);
    const int CoinSize=76;
    readonly string[] sounds={"anticipation","attack","failure","success","theme","gold","spawn","glitch"};

    [STAThread] static void Main(){
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using(Rans0m game=new Rans0m())Application.Run(game);
    }
    Rans0m():this(Array.IndexOf(Environment.GetCommandLineArgs(), "--windowed")<0){}
    Rans0m(bool useDesktop){
        desktopMode=useDesktop;
        timer=new FrameTimer(this);
        Text="RANS0M v18";
        FormBorderStyle=desktopMode?FormBorderStyle.None:FormBorderStyle.FixedSingle;
        StartPosition=desktopMode?FormStartPosition.Manual:FormStartPosition.CenterScreen;
        if(desktopMode)Bounds=Screen.FromPoint(Cursor.Position).Bounds;
        else {
            Rectangle area=Screen.PrimaryScreen.WorkingArea;
            ClientSize=new Size(Math.Max(640,Math.Min(1200,area.Width-40)),Math.Max(520,Math.Min(800,area.Height-80)));
            MaximizeBox=false;
        }
        BackColor=desktopMode?transparent:Color.FromArgb(15,17,24);
        if(desktopMode)TransparencyKey=transparent;
        TopMost=desktopMode;
        ShowInTaskbar=true;
        KeyPreview=true;
        DoubleBuffered=true;
        try {
        audioClips.Add("anticipation",new AudioClip(Assembly.GetExecutingAssembly().GetManifestResourceStream("anticipation.wav")));
        applicationIcon=System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        Icon=applicationIcon;
        face=ReadImage("face.png",false);
        jumpscare=ReadImage("jumpscare.png",false);
        popupFace=ReadImage("popup-face.png",false);
        using(Bitmap originalCoin=ReadImage("coin.png",false)){
            if(originalCoin!=null){
                coinImage=new Bitmap(CoinSize,CoinSize,PixelFormat.Format32bppArgb);
                coinImage.SetResolution(96,96);
                using(Graphics g=Graphics.FromImage(coinImage)){
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    g.DrawImage(originalCoin,new Rectangle(0,0,CoinSize,CoinSize),0,0,originalCoin.Width,originalCoin.Height,GraphicsUnit.Pixel);
                }
            }
        }
        if(coinImage!=null){
            purpleCoin=new Bitmap(CoinSize,CoinSize,PixelFormat.Format32bppArgb);
            using(Graphics g=Graphics.FromImage(purpleCoin))using(ImageAttributes tint=new ImageAttributes()){
                tint.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[]{.30f,.08f,.45f,0,0},new float[]{.60f,.18f,.85f,0,0},new float[]{.10f,.03f,.15f,0,0},
                    new float[]{0,0,0,1,0},new float[]{.08f,0,.12f,0,1}}));
                g.DrawImage(coinImage,new Rectangle(0,0,CoinSize,CoinSize),0,0,CoinSize,CoinSize,GraphicsUnit.Pixel,tint);
            }
        }
        using(Bitmap originalRare=ReadImage("rare-gold.png",false)){
            if(originalRare!=null){
                rareGold=new Bitmap(CoinSize,CoinSize,PixelFormat.Format32bppArgb);
                float scale=Math.Min((float)(CoinSize-8)/originalRare.Width,(float)(CoinSize-8)/originalRare.Height);
                int width=(int)(originalRare.Width*scale),height=(int)(originalRare.Height*scale);
                using(Graphics g=Graphics.FromImage(rareGold)){
                    g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    g.DrawImage(originalRare,new Rectangle((CoinSize-width)/2,CoinSize-8-height,width,height),0,0,originalRare.Width,originalRare.Height,GraphicsUnit.Pixel);
                }
            }
        }
        victory=ReadImage("thankyou.png",false);
        stopSign=ReadImage("stop.png",false);
        foreach(string name in sounds)if(name!="anticipation")audioClips.Add(name,new AudioClip(Assembly.GetExecutingAssembly().GetManifestResourceStream(name+".wav")));
        failureView=new FailureView(this);failureView.Prepare();
        } catch {Dispose();throw;}
        Load+=delegate {
            phaseStarted=Now();
            startingMouse=Cursor.Position;
            introSoundStarted=false;
            introEnds=phaseStarted+IntroFaceDuration+SoundDuration("anticipation");
        };
        Shown+=delegate {
            preciseTimer=timeBeginPeriod(1)==0;
            timer.Interval=1; // Deadline-paced 60 FPS; animation uses elapsed time.
            timer.Tick+=Frame;
            timer.Start();
            Activate();
        };
        Deactivate+=delegate {startingMouse=Cursor.Position;};
        Activated+=delegate {startingMouse=Cursor.Position;};
        KeyDown+=delegate(object s,KeyEventArgs e){
            if(e.KeyCode==Keys.Escape)Close();
            else if(e.KeyCode==Keys.M){muted=!muted;if(muted)StopAll();else if(phase==3&&audioClips.ContainsKey("theme"))audioClips["theme"].PlayFrom(Now()-phaseStarted);Invalidate();}
            else if(e.KeyCode==Keys.Y){FastForwardToTen();e.SuppressKeyPress=true;}
            else if(e.KeyCode==Keys.R){reducedMotion=!reducedMotion;Invalidate();}
            else if(e.KeyCode==Keys.Enter&&phase==5)DismissVictory();
            else if(e.KeyCode==Keys.Enter&&phase==0)StartGame();
        };
        MouseDown+=ClickCoin;
        MouseUp+=delegate {draggingRansom=false;Capture=false;};
        MouseCaptureChanged+=delegate {if(!Capture)draggingRansom=false;};
        MouseMove+=delegate(object sender,MouseEventArgs e){
            if(draggingRansom){
                Rectangle panel=RansomPanel();
                ransomPosition=new Point(Math.Max(0,Math.Min(ClientSize.Width-panel.Width,e.X-ransomDragOffset.X)),Math.Max(0,Math.Min(ClientSize.Height-panel.Height,e.Y-ransomDragOffset.Y)));
                Invalidate();return;
            }
            Point point=PointToScreen(e.Location);
            if(phase==0&&ContainsFocus&&ClientRectangle.Contains(e.Location)&&(Math.Abs(point.X-startingMouse.X)>5||Math.Abs(point.Y-startingMouse.Y)>5))StartGame();
        };
    }
    protected override void WndProc(ref Message message){
        if(message.Msg==FrameTimer.Message&&timer!=null){timer.Dispatch(message.WParam.ToInt32());return;}
        base.WndProc(ref message);
    }
    Bitmap ReadImage(string resource,bool clearMagenta){
        using(Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)){
            if(s==null)return null;
            using(Bitmap original=new Bitmap(s)){
                Bitmap result=new Bitmap(original.Width,original.Height,PixelFormat.Format32bppArgb);
                using(Graphics g=Graphics.FromImage(result))g.DrawImage(original,new Rectangle(0,0,result.Width,result.Height),0,0,original.Width,original.Height,GraphicsUnit.Pixel);
                if(clearMagenta){
                    BitmapData pixels=result.LockBits(new Rectangle(0,0,result.Width,result.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
                    try {
                        byte[] bytes=new byte[pixels.Stride*result.Height];
                        Marshal.Copy(pixels.Scan0,bytes,0,bytes.Length);
                        for(int i=0;i<bytes.Length;i+=4)if(bytes[i+2]>140&&bytes[i]>100&&bytes[i+1]<bytes[i+2]/2)bytes[i+3]=0;
                        Marshal.Copy(bytes,0,pixels.Scan0,bytes.Length);
                    } finally {result.UnlockBits(pixels);}
                }
                return result;
            }
        }
    }
    void StopAll(){foreach(AudioClip clip in audioClips.Values)clip.Stop();}
    void StartSound(string name,bool loop,bool exclusive){
        if(exclusive)StopAll();
        double started=spawnClockOverride??Now();
        soundStarts[name]=started;
        if(!muted&&audioClips.ContainsKey(name)){
            double offset=Math.Max(0,Now()-started);
            if(offset<audioClips[name].Duration){if(offset>.001)audioClips[name].PlayFrom(offset);else audioClips[name].Play(loop);}
        }
    }
    double SoundDuration(string name){return audioClips.ContainsKey(name)?audioClips[name].Duration:1;}
    static double Ease(double v){v=Math.Max(0,Math.Min(1,v));return v*v*(3-2*v);}
    double Now(){return watch.Elapsed.TotalSeconds+timeOffset;}
    void SetPhase(int p){
        if(p!=4&&failureView!=null&&!failureView.IsDisposed)failureView.Hide();
        double previousAge=Now()-phaseStarted;
        int previousPhase=phase;
        phase=p;phaseStarted=Now();
        if(p==5){secondsAtWin=Math.Max(0,90-previousAge);closeCallWin=previousPhase==3&&secondsAtWin<5;}
        if(p==1){
            Invalidate();Update();
            StartSound("attack",false,true);attackStarted=Now();phaseStarted=attackStarted;shakeUntil=Now()+AttackFaceDuration;
        }
        if(p==3){
            StartSound("theme",false,false); // Preserve the slash and its tail in the combined clip.
            balance=0;combo=0;coins.Clear();coinRespawns.Clear();respawnZones.Clear();coinRespawnKinds.Clear();
            for(int i=0;i<MaxCoins+MaxGlitchCoins;i++){bool fake=i>=MaxCoins;Gold gold=NewCoin(fake);if(gold!=null)AddCoin(gold);else {coinRespawns.Add(Now()+.5);respawnZones.Add(Rectangle.Empty);coinRespawnKinds.Add(fake);}}
            ShowMonitorTints();
            openingPopups=desktopMode?28:16;openingPopupAt=Now();
            lastCountdown=-1;lastShownBalance=-1;lastShownCombo=-1;
            nextPopupAt=Now()+1.2;nextReplacementAt=Now()+20;nextBurstAt=Now()+12;nextTheftAt=Now()+3;nextWaveAt=Now()+9;nextTimerGlitchAt=Now()+7;timerGlitchUntil=0;coinBursts.Clear();waveStarted=null;cursorTrail.Clear();
            shakeUntil=Now()+.38;
            SpawnToast("RANSOM ACTIVE - 500 GOLD");
        }
        if(p==4){
            coins.Clear();coinRespawns.Clear();respawnZones.Clear();coinRespawnKinds.Clear();
            stoppedFailureExtras=false;
            if(failureView==null||failureView.IsDisposed){failureView=new FailureView(this);failureView.Prepare();}
            failureView.Place();failureView.Show(this);failureView.Update();
            StartSound("failure",false,false);
            StopWave();
            phaseStarted=Now();
            // The scare is already visible and audible before touching dozens of windows.
            Invalidate();Update();
            foreach(MonitorTint tint in monitorTints)if(!tint.IsDisposed)tint.Opacity=0;
            foreach(FakePopup popup in popups)if(!popup.IsDisposed)popup.Hide();
        }
        if(p==5){ClearMonitorTints();coins.Clear();coinRespawns.Clear();respawnZones.Clear();coinRespawnKinds.Clear();BeginClosePopups();foreach(KeyValuePair<string,AudioClip> entry in audioClips)if(entry.Key!="gold")entry.Value.Stop();StartSound("success",false,false);}
        Invalidate();
    }
    void DismissVictory(){
        // Ignore the remaining clicks from the winning combo until its audio finishes.
        if(Now()-phaseStarted>=SoundDuration("success"))Close();
    }
    void StartStopSignSound(){
        if(phase!=0||introSoundStarted)return;
        introSoundStarted=true;
        StartSound("anticipation",false,true);
        introEnds=Now()+SoundDuration("anticipation");
    }
    void FinishIdleIntro(){
        // The visual lasts one second; its closing glitch is allowed to finish.
        phase=6;Hide();
        if(Now()>=introEnds)Close();
    }
    void StartGame(){
        if(phase==5||phase==4)return;
        if(phase==0&&Now()-phaseStarted>=1){FinishIdleIntro();return;}
        ClearMonitorTints();
        foreach(FakePopup f in popups)if(!f.IsDisposed)f.Dispose();
        popups.Clear(); coins.Clear();coinRespawns.Clear();respawnZones.Clear();coinRespawnKinds.Clear(); balance=0; combo=0; popupSequence=0;popupMonitorIndex=0;
        ransomPosition=null;draggingRansom=false;
        lastCoinAt=-10; lastToast=-10; shakeUntil=0; cursorUntil=0; displayedBalance=0;pickups.Clear();
        SetPhase(1);
    }
    void ShowMonitorTints(){
        ClearMonitorTints();
        if(!desktopMode||phase!=3)return;
        foreach(Screen screen in Screen.AllScreens){
            MonitorTint tint=new MonitorTint(screen.Bounds);
            monitorTints.Add(tint);tint.Show(this);
        }
    }
    void ClearMonitorTints(){
        StopWave();
        foreach(MonitorTint tint in monitorTints)if(!tint.IsDisposed)tint.Dispose();
        monitorTints.Clear();
    }
    void BeginClosePopups(){
        foreach(FakePopup popup in popups)if(!popup.IsDisposed)popup.BeginFold(Now());
    }
    Rectangle RansomPanel(){return new Rectangle(ransomPosition??new Point(Math.Max(0,ClientSize.Width-490),Math.Max(0,ClientSize.Height-350)),new Size(480,340));}
    bool CoinFits(Point point){return CoinFits(point,null);}
    bool CoinFits(Point point,Gold ignore){
        Rectangle proposed=new Rectangle(point.X-6,point.Y-6,CoinSize+12,CoinSize+12);
        if(!ClientRectangle.Contains(proposed)||point.Y<55||proposed.IntersectsWith(RansomPanel()))return false;
        foreach(Gold gold in coins)if(gold!=ignore&&proposed.IntersectsWith(new Rectangle(gold.Position,new Size(CoinSize,CoinSize))))return false;
        foreach(FakePopup popup in popups)if(!popup.IsDisposed&&proposed.IntersectsWith(new Rectangle(popup.FinalPosition,popup.Size)))return false;
        return true;
    }
    void RefillCoins(double now){
        if(phase!=3)return;
        for(int i=coinRespawns.Count-1;i>=0;i--){
            bool fake=coinRespawnKinds[i];
            if(coinRespawns[i]>now||CoinCount(fake)>=(fake?MaxGlitchCoins:MaxCoins))continue;
            Gold gold=NewCoin(fake);
            if(gold==null&&!respawnZones[i].IsEmpty){
                Point reserved=new Point(respawnZones[i].X+7,respawnZones[i].Y+7);
                if(CoinFits(reserved))gold=CreateCoin(reserved,!fake&&random.NextDouble()<RareCoinChance,fake);
            }
            if(gold==null){coinRespawns[i]=now+.25;continue;}
            AddCoin(gold);coinRespawns.RemoveAt(i);respawnZones.RemoveAt(i);coinRespawnKinds.RemoveAt(i);
        }
    }
    int CoinCount(bool fake){
        int count=0;foreach(Gold gold in coins)if(gold.IsFake==fake)count++;return count;
    }
    void AddCoin(Gold gold){
        if(CoinCount(gold.IsFake)>=(gold.IsFake?MaxGlitchCoins:MaxCoins))return;
        gold.Birth=spawnClockOverride??Now();gold.NextMove=gold.Birth+5+random.NextDouble()*5;
        coins.Add(gold);StartSound("spawn",false,false);Invalidate(Rectangle.Inflate(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)),8,8));
    }
    Gold CreateCoin(Point point,bool rare,bool fake){
        Gold gold=new Gold(point,rare);gold.Decoy=fake;gold.Moves=!rare&&random.NextDouble()<.35;return gold;
    }
    Gold NewCoin(){return NewCoin(false);}
    Gold NewCoin(bool fake){
        bool rare=!fake&&random.NextDouble()<RareCoinChance;
        int right=Math.Max(13,ClientSize.Width-CoinSize-12),bottom=Math.Max(56,ClientSize.Height-CoinSize-12);
        for(int i=0;i<256;i++){
            Point point=new Point(random.Next(12,right),random.Next(55,bottom));
            if(CoinFits(point))return CreateCoin(point,rare,fake);
        }
        // Never fall back to an overlapping or off-screen center position.
        for(int y=55;y<bottom;y+=6)for(int x=12;x<right;x+=6){
            Point point=new Point(x,y);if(CoinFits(point))return CreateCoin(point,rare,fake);
        }
        return null;
    }
    static Rectangle[] MapPopupMonitors(Point gameOrigin,Rectangle[] monitors){
        Rectangle[] areas=new Rectangle[monitors.Length];
        for(int i=0;i<monitors.Length;i++)areas[i]=new Rectangle(monitors[i].X-gameOrigin.X,monitors[i].Y-gameOrigin.Y,monitors[i].Width,monitors[i].Height);
        return areas;
    }
    Rectangle[] PopupAreas(){
        if(!desktopMode)return new Rectangle[]{ClientRectangle};
        Screen[] screens=Screen.AllScreens;
        Rectangle[] monitors=new Rectangle[screens.Length];
        for(int i=0;i<screens.Length;i++)monitors[i]=screens[i].WorkingArea;
        return MapPopupMonitors(PointToScreen(Point.Empty),monitors);
    }
    void SpawnPopup(bool toast,double delay){SpawnPopup(toast,delay,null);}
    void SpawnPopup(bool toast,double delay,string message){
        popups.RemoveAll(delegate(FakePopup closed){return closed.IsDisposed;});
        int active=0;foreach(FakePopup item in popups)if(!item.Exiting)active++;
        if(phase!=3||active>=PopupLimit()+(popupExpiryOverride.HasValue?4:0))return;
        Rectangle[] areas=PopupAreas();
        Rectangle area=areas[(popupMonitorIndex++)%areas.Length];
        int w=area.Width,h=area.Height;
        int pw=toast?Math.Min(330,Math.Max(195,w/3)):random.Next(165,260);
        int ph=toast?110:random.Next(110,175);
        Rectangle bounds=Rectangle.Empty;
        for(int attempt=0;attempt<80;attempt++){
            int x=area.X+random.Next(8,Math.Max(9,w-pw-8));
            int y=area.Y+random.Next(40,Math.Max(41,h-ph-8));
            Rectangle candidate=new Rectangle(x,y,pw,ph);
            if(candidate.IntersectsWith(RansomPanel()))continue;
            bool covered=false;
            foreach(Gold gold in coins)if(candidate.IntersectsWith(new Rectangle(gold.Position.X-7,gold.Position.Y-7,CoinSize+14,CoinSize+14))){covered=true;break;}
            foreach(Rectangle zone in respawnZones)if(candidate.IntersectsWith(zone)){covered=true;break;}
            if(!covered){bounds=candidate;break;}
        }
        if(bounds.IsEmpty)return; // Cosmetic effects never take space away from gameplay.
        if(popupSequence%10==9){
            foreach(FakePopup existing in popups)if(existing.Steals&&!existing.IsDisposed&&!existing.Exiting){popupSequence++;break;}
        }
        FakePopup popup=new FakePopup(popupSequence++,bounds,(spawnClockOverride??Now())+delay,popupFace,toast,message);
        if(popupExpiryOverride.HasValue)popup.ExpiresAt=popupExpiryOverride.Value;
        popups.Add(popup);
        popup.Location=PointToScreen(popup.FinalPosition);
        popup.Show(this);
        popup.Opacity=0;
    }
    void SpawnToast(string message){
        // All notifications are deliberately fake and drawn by the game.
        if(Now()-lastToast<.45)return;
        lastToast=Now();
        SpawnPopup(true,0,message);
    }
    static double Escalation(double age){return Math.Max(0,Math.Min(1,(age-75)/15));}
    int PopupLimit(){return phase==3&&(spawnClockOverride??Now())-phaseStarted>=75?64:MaxPopups;}
    void SpawnBurst(double now){
        if(phase!=3)return;
        popupExpiryOverride=now+3.5;
        try {for(int i=0;i<4;i++)SpawnPopup(false,i*.045);}
        finally {popupExpiryOverride=null;}
    }
    void RecyclePopup(){
        if(phase!=3)return;
        for(int i=0;i<popups.Count;i++)if(!popups[i].IsDisposed&&!popups[i].Exiting){
            popups[i].BeginFold(Now());break;
        }
        SpawnPopup(false,0);
    }
    void DriftPopup(FakePopup popup,double now){
        if(reducedMotion||!popup.MayDrift||popup.HasBeenDragged||popup.Dragging||popup.Exiting||now-popup.Birth<.5)return;
        double time=(now-popup.Birth-.5)*(1.1+Escalation(now-phaseStarted)*.6);
        Point proposed=new Point(popup.DriftOrigin.X+(int)(Math.Sin(time)*60),popup.DriftOrigin.Y+(int)(Math.Sin(time*.7)*32));
        Rectangle candidate=new Rectangle(proposed,popup.Size);
        if(candidate.IntersectsWith(RansomPanel()))return;
        foreach(Gold gold in coins)if(candidate.IntersectsWith(new Rectangle(gold.Position.X-7,gold.Position.Y-7,CoinSize+14,CoinSize+14)))return;
        foreach(Rectangle zone in respawnZones)if(candidate.IntersectsWith(zone))return;
        Rectangle monitor=desktopMode?RectangleToClient(Screen.FromRectangle(RectangleToScreen(candidate)).WorkingArea):ClientRectangle;
        if(monitor.Contains(candidate))popup.SetDriftPosition(proposed);
    }
    void FastForwardToTen(){
        if(phase!=3)return;
        double skip=80-(Now()-phaseStarted);
        if(skip<=0)return;
        StopAll();
        timeOffset+=skip;
        double target=Now();
        try {
            while(openingPopups>0&&openingPopupAt<=target){
                spawnClockOverride=openingPopupAt;SpawnPopup(false,0);openingPopups--;openingPopupAt+=.035;
            }
            // Pending pickups and popup cycles share the same virtual timeline.
            if(coinRespawns.Count>0){
                double earliest=coinRespawns[0];
                foreach(double due in coinRespawns)earliest=Math.Min(earliest,due);
                spawnClockOverride=earliest;RefillCoins(target);
            }
            while(Math.Min(nextTheftAt,Math.Min(nextBurstAt,Math.Min(nextPopupAt,nextReplacementAt)))<=target){
                if(nextTheftAt<=nextBurstAt&&nextTheftAt<=nextPopupAt&&nextTheftAt<=nextReplacementAt){
                    double scheduledTheft=nextTheftAt;spawnClockOverride=scheduledTheft;StealGold(scheduledTheft);continue;
                }
                if(nextBurstAt<=nextPopupAt&&nextBurstAt<=nextReplacementAt){
                    spawnClockOverride=nextBurstAt;SpawnBurst(nextBurstAt);nextBurstAt+=12;continue;
                }
                bool replace=nextReplacementAt<=nextPopupAt;
                double scheduled=replace?nextReplacementAt:nextPopupAt;
                spawnClockOverride=scheduled;
                if(replace){RecyclePopup();nextReplacementAt+=20;}
                else {SpawnPopup(false,0);nextPopupAt+=1.2-.75*Escalation(scheduled-phaseStarted);}
            }
        } finally {spawnClockOverride=null;}
        timeOffset-=Now()-target; // UI was blocked during catch-up; return with ten seconds left.
        foreach(KeyValuePair<string,AudioClip> clip in audioClips){
            double started;
            if(!muted&&soundStarts.TryGetValue(clip.Key,out started)){
                double offset=Now()-started;
                if(offset>=0&&offset<clip.Value.Duration)clip.Value.PlayFrom(offset);
            }
        }
        previousFrame=0;nextFrame=0;
        Frame(this,EventArgs.Empty);
    }
    static int WaveX(Rectangle desktop,double progress){return desktop.Left-32+(int)((desktop.Width+64)*Math.Max(0,Math.Min(1,progress)));}
    void StopWave(){
        waveStarted=null;
        if(corruptionWave!=null&&!corruptionWave.IsDisposed)corruptionWave.Hide();
    }
    void UpdateWave(double now){
        if(phase!=3||reducedMotion){StopWave();nextWaveAt=now+12;return;}
        Rectangle desktop=desktopMode?SystemInformation.VirtualScreen:RectangleToScreen(ClientRectangle);
        if(!waveStarted.HasValue&&now>=nextWaveAt){
            waveStarted=now;
            if(corruptionWave==null||corruptionWave.IsDisposed)corruptionWave=new CorruptionWave();
            corruptionWave.Bounds=new Rectangle(desktop.Left-32,desktop.Top,32,desktop.Height);
            corruptionWave.Show(this);
        }
        if(!waveStarted.HasValue)return;
        double progress=(now-waveStarted.Value)/(now-phaseStarted>=75?2.5:3.5);
        if(progress>=1){StopWave();nextWaveAt=now+14;return;}
        int x=WaveX(desktop,progress);
        if(now-lastWaveMove>=1.0/30){
            corruptionWave.Location=new Point(x,desktop.Top);lastWaveMove=now;
        }
        Rectangle scan=new Rectangle(x,desktop.Top,32,desktop.Height);
        foreach(FakePopup popup in popups)if(!popup.IsDisposed&&!popup.Exiting&&scan.IntersectsWith(popup.Bounds))popup.CorruptUntil=now+.22;
        for(int i=coins.Count-1;i>=0;i--){
            if(i>=coins.Count)continue;
            Gold coin=coins[i];
            if(coin.IsFake&&scan.IntersectsWith(RectangleToScreen(new Rectangle(coin.Position,new Size(CoinSize,CoinSize)))))BurstGlitchCoin(coin,now);
        }
    }
    void BurstGlitchCoin(Gold coin,double now){
        if(phase!=3||!coin.IsFake||!coins.Remove(coin))return;
        Point center=new Point(coin.Position.X+CoinSize/2,coin.Position.Y+CoinSize/2);
        coinBursts.Add(new CoinBurst(center,now));
        Invalidate(new Rectangle(center.X-170,center.Y-170,340,340));
        foreach(CursorTrace trace in coin.Trail)Invalidate(new Rectangle(trace.Position,new Size(CoinSize,CoinSize)));
        coinRespawns.Add(now+2.5+random.NextDouble()*1.5);coinRespawnKinds.Add(true);
        respawnZones.Add(new Rectangle(coin.Position.X-7,coin.Position.Y-7,CoinSize+14,CoinSize+14));
        Rectangle blast=new Rectangle(center.X-220,center.Y-220,440,440);
        foreach(FakePopup popup in popups)if(!popup.IsDisposed&&!popup.Exiting&&blast.IntersectsWith(new Rectangle(popup.FinalPosition,popup.Size)))popup.CorruptUntil=Math.Max(popup.CorruptUntil,now+.6);
        for(int i=coins.Count-1;i>=0;i--){
            if(i>=coins.Count)continue;
            Gold next=coins[i];int dx=next.Position.X+CoinSize/2-center.X,dy=next.Position.Y+CoinSize/2-center.Y;
            if(next.IsFake&&dx*dx+dy*dy<180*180)BurstGlitchCoin(next,now);
        }
    }
    static string CountdownVisual(int left,bool corrupted,double time){
        string real=(left/60).ToString("00")+":"+(left%60).ToString("00");
        if(!corrupted)return real;
        char[] text=real.ToCharArray();int tick=(int)(time*24);
        text[(tick&1)==0?1:3]="#?%"[(tick/2)%3];text[4]="89#?"[tick%4];
        return new string(text);
    }
    void DrawCoinBursts(Graphics g,double now){
        foreach(CoinBurst burst in coinBursts){
            double age=now-burst.Birth,t=Math.Max(0,Math.Min(1,age/.55));
            int alpha=(int)(220*(1-t));float radius=reducedMotion?24:(float)(18+Ease(t)*125);
            using(Pen ring=new Pen(Color.FromArgb(alpha,180,60,255),2))g.DrawEllipse(ring,burst.Center.X-radius,burst.Center.Y-radius,radius*2,radius*2);
            if(!reducedMotion)using(Brush shards=new SolidBrush(Color.FromArgb(alpha,230,140,255))){
                for(int i=0;i<10;i++){
                    double angle=i*Math.PI*2/10;
                    g.FillRectangle(shards,burst.Center.X+(float)Math.Cos(angle)*radius-2,burst.Center.Y+(float)Math.Sin(angle)*radius-2,5,3);
                }
            }
        }
    }
    void StealGold(double now){
        if(phase!=3||now<nextTheftAt)return;
        nextTheftAt=now+3;
        foreach(FakePopup popup in popups)if(popup.Steals&&!popup.IsDisposed&&!popup.Exiting&&now<popup.ExpiresAt){
            int stolen=Math.Min(5,balance);
            if(stolen>0){
                balance-=stolen;Rectangle panel=RansomPanel();
                pickups.Add(new Pickup(new Point(panel.X+50,panel.Y+100),-stolen,now,false));
                Invalidate(panel);
            }
            break;
        }
    }
    bool CoinMoveFits(Gold gold,Point point){
        if(!CoinFits(point,gold))return false;
        Rectangle bounds=new Rectangle(point.X-6,point.Y-6,CoinSize+12,CoinSize+12);
        foreach(Rectangle reserved in respawnZones)if(bounds.IntersectsWith(reserved))return false;
        return true;
    }
    void MoveCoins(double now){
        if(phase!=3)return;
        int moving=0;foreach(Gold active in coins)if(active.Target.HasValue)moving++;
        foreach(Gold gold in coins){
            foreach(CursorTrace trace in gold.Trail)Invalidate(new Rectangle(trace.Position,new Size(CoinSize,CoinSize)));
            gold.Trail.RemoveAll(delegate(CursorTrace trace){return now-trace.Birth>.3;});
            if(reducedMotion){gold.Target=null;gold.Trail.Clear();continue;}
            if(!gold.Moves)continue;
            if(gold.Target.HasValue){
                double progress=Ease((now-gold.MoveStarted)/.8);
                Point target=gold.Target.Value;
                Point next=new Point((int)(gold.From.X+(target.X-gold.From.X)*progress),(int)(gold.From.Y+(target.Y-gold.From.Y)*progress));
                if(!CoinMoveFits(gold,next)){gold.Target=null;gold.NextMove=now+4;continue;}
                if(next!=gold.Position){
                    Invalidate(Rectangle.Inflate(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)),8,8));
                    if(gold.Trail.Count==0||now-gold.Trail[gold.Trail.Count-1].Birth>.04)gold.Trail.Add(new CursorTrace(gold.Position,now));
                    gold.Position=next;Invalidate(Rectangle.Inflate(new Rectangle(next,new Size(CoinSize,CoinSize)),8,8));
                }
                if(progress>=1){moving--;gold.Target=null;gold.NextMove=now+6+random.NextDouble()*4;}
            } else if(moving<2&&now>=gold.NextMove){
                gold.NextMove=now+3;
                for(int attempt=0;attempt<30;attempt++){
                    Point target=new Point(gold.Position.X+random.Next(-110,111),gold.Position.Y+random.Next(-75,76));
                    if(Math.Abs(target.X-gold.Position.X)+Math.Abs(target.Y-gold.Position.Y)<45)continue;
                    bool clear=true;
                    for(int step=1;step<=8;step++)if(!CoinMoveFits(gold,new Point(gold.Position.X+(target.X-gold.Position.X)*step/8,gold.Position.Y+(target.Y-gold.Position.Y)*step/8))){clear=false;break;}
                    if(clear){gold.From=gold.Position;gold.Target=target;gold.MoveStarted=now;moving++;break;}
                }
            }
        }
    }
    void UpdateCursorTrail(double now){
        foreach(CursorTrace trace in cursorTrail)Invalidate(new Rectangle(trace.Position.X-18,trace.Position.Y-18,36,36));
        cursorTrail.RemoveAll(delegate(CursorTrace trace){return now-trace.Birth>.28;});
        if(phase!=3||reducedMotion||now-phaseStarted<75){cursorTrail.Clear();return;}
        Point pointer=PointToClient(Cursor.Position);
        if(cursorTrail.Count==0||pointer!=cursorTrail[cursorTrail.Count-1].Position){
            if(cursorTrail.Count>=12)cursorTrail.RemoveAt(0);
            cursorTrail.Add(new CursorTrace(pointer,now));
            Invalidate(new Rectangle(pointer.X-18,pointer.Y-18,36,36));
        }
    }
    void Frame(object sender,EventArgs args){
        double now=Now();
        if(now<nextFrame)return;
        // Advance the deadline rather than accumulating timer scheduling jitter.
        double interval=WindowState==FormWindowState.Minimized?.1:FrameDuration;
        nextFrame+=interval;
        if(nextFrame<=now)nextFrame=now+interval;
        double delta=previousFrame==0?FrameDuration:Math.Min(.25,Math.Max(0,now-previousFrame));
        previousFrame=now;
        double age=now-phaseStarted;
        if((desktopMode||ContainsFocus)&&(GetAsyncKeyState(27)&0x8000)!=0){Close();return;}
        if(desktopMode&&phase==3){
            bool down=(GetAsyncKeyState((int)Keys.Y)&0x8000)!=0;
            bool pressed=down&&!yWasDown;yWasDown=down;
            if(pressed){FastForwardToTen();return;}
        }
        if(phase==0&&desktopMode&&(Math.Abs(Cursor.Position.X-startingMouse.X)>5||Math.Abs(Cursor.Position.Y-startingMouse.Y)>5))StartGame();
        age=now-phaseStarted;
        if(phase==0&&age>=1){FinishIdleIntro();return;}
        if(phase==6){if(now>=introEnds)Close();return;}
        if(phase==1&&age>=AttackFaceDuration)SetPhase(2);
        else if(phase==2&&now-attackStarted>=AttackSlashTime)SetPhase(3);
        else if(phase==3&&age>=90)SetPhase(4);
        if(phase==4&&Now()-phaseStarted>=SoundDuration("failure")+1){Close();return;}
        MoveCoins(now);UpdateWave(now);StealGold(now);UpdateCursorTrail(now);
        RefillCoins(now);
        if(phase==3&&openingPopups>0&&now>=openingPopupAt){SpawnPopup(false,0);openingPopups--;openingPopupAt=now+.035;}
        if(phase==3&&now>=nextPopupAt){SpawnPopup(false,0);nextPopupAt=now+1.2-.75*Escalation(now-phaseStarted);}
        if(phase==3&&now>=nextReplacementAt){RecyclePopup();nextReplacementAt=now+20;}
        if(phase==3&&now>=nextTimerGlitchAt){timerGlitchUntil=now+.25;nextTimerGlitchAt=now+8+random.NextDouble()*6;}
        foreach(CoinBurst burst in coinBursts)Invalidate(new Rectangle(burst.Center.X-170,burst.Center.Y-170,340,340));
        coinBursts.RemoveAll(delegate(CoinBurst burst){return now-burst.Birth>.55;});
        if(phase==3&&now>=nextBurstAt){SpawnBurst(now);nextBurstAt=now+12+random.NextDouble()*4;}
        if(phase==3){
            double tint=.18+.12*Escalation(now-phaseStarted);
            foreach(MonitorTint overlay in monitorTints)if(Math.Abs(overlay.Opacity-tint)>.005)overlay.Opacity=tint;
        }
        if(!reducedMotion&&phase==3&&now>cursorUntil+7.0&&random.NextDouble()<.15)cursorUntil=now+.45;
        displayedBalance+=(balance-displayedBalance)*(1-Math.Exp(-delta*14));
        if(phase==3&&now-lastCoinAt>1.15)combo=0;
        foreach(Pickup effect in pickups)Invalidate(new Rectangle(effect.Position.X-90,effect.Position.Y-90,180,120));
        pickups.RemoveAll(delegate(Pickup effect){return now-effect.Birth>.7;});
        if(phase==4){
            if(failureView!=null&&!failureView.IsDisposed)failureView.Invalidate();
            for(int i=0;i<3&&popups.Count>0;i++){FakePopup old=popups[popups.Count-1];popups.RemoveAt(popups.Count-1);if(!old.IsDisposed)old.Dispose();}
            if(!stoppedFailureExtras&&now-phaseStarted>.05){
                stoppedFailureExtras=true;
                foreach(KeyValuePair<string,AudioClip> clip in audioClips)if(clip.Key!="failure")clip.Value.Stop();
            }
        }
        if(WindowState!=FormWindowState.Minimized){
            if(phase!=4)AnimatePopups(now);
            if(phase!=3)Invalidate();
            else InvalidateGameplay(now);
        }
    }
    void InvalidateGameplay(double now){
        foreach(Gold gold in coins)if(!reducedMotion&&now-gold.Birth<.35)Invalidate(Rectangle.Inflate(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)),8,8));
        bool timerCorrupt=!reducedMotion&&now<timerGlitchUntil;
        if(timerCorrupt||timerWasGlitched)Invalidate(new Rectangle(RansomPanel().X+160,RansomPanel().Y+263,310,44));
        timerWasGlitched=timerCorrupt;
        int countdown=(int)Math.Ceiling(Math.Max(0,90-(now-phaseStarted)));
        int shown=(int)Math.Round(displayedBalance);
        if(countdown!=lastCountdown||shown!=lastShownBalance||combo!=lastShownCombo){
            Invalidate(Rectangle.Inflate(RansomPanel(),8,8));
            lastCountdown=countdown;lastShownBalance=shown;lastShownCombo=combo;
        }
        Point pointer=PointToClient(Cursor.Position);
        if(pointer!=previousPointer||now<cursorUntil||handVisible){
            Invalidate(new Rectangle(previousPointer.X-55,previousPointer.Y-55,110,110));
            Invalidate(new Rectangle(pointer.X-55,pointer.Y-55,110,110));
            foreach(Gold gold in coins)if(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)).Contains(pointer)||new Rectangle(gold.Position,new Size(CoinSize,CoinSize)).Contains(previousPointer))Invalidate(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)));
            previousPointer=pointer;handVisible=now<cursorUntil;
        }
        if(!reducedMotion&&now-lastWorldGlitch>=.125){
            lastWorldGlitch=now;
            Invalidate(new Rectangle(0,45,ClientSize.Width/5+65,ClientSize.Height-45));
        }
        if(!reducedMotion&&now-lastCoinGlitch>=.08){
            lastCoinGlitch=now;
            foreach(Gold gold in coins)if(gold.IsFake)Invalidate(Rectangle.Inflate(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)),8,8));
        }
    }
    void AnimatePopups(double now){
        int animated=0,drifting=0;
        for(int i=popups.Count-1;i>=0;i--){
            FakePopup f=popups[i];
            if(f.IsDisposed){popups.RemoveAt(i);continue;}
            if(!f.Exiting&&now>=f.ExpiresAt)f.BeginFold(f.ExpiresAt);
            if(f.IsDisposed){popups.RemoveAt(i);continue;}
            if(f.MayDrift&&!f.HasBeenDragged&&drifting++<4)DriftPopup(f,now);
            double alpha;
            if(f.Exiting){
                double t=reducedMotion?1:Math.Max(0,(now-f.ExitingAt)/.38);
                if(t>=1){f.Close();popups.RemoveAt(i);continue;}
                int height=Math.Max(3,(int)(f.FoldSize.Height*(1-Ease(t/.8))));
                int width=Math.Max(3,(int)(f.FoldSize.Width*(1-Ease((t-.8)/.2))));
                Point center=PointToScreen(new Point(f.FoldOrigin.X+f.FoldSize.Width/2,f.FoldOrigin.Y+f.FoldSize.Height/2));
                Rectangle folded=new Rectangle(center.X-width/2,center.Y-height/2,width,height);
                if(f.Bounds!=folded)f.Bounds=folded;
                alpha=1-t*.4;
            } else {
                double t=reducedMotion?1:Ease((now-f.Birth)/.50);
                alpha=t;
                Point location=PointToScreen(new Point(f.FinalPosition.X-(int)((1-t)*70),f.FinalPosition.Y));
                if(!f.Dragging&&!f.HasBeenDragged&&f.Location!=location)f.Location=location;
                else if(!f.Dragging&&f.HasBeenDragged){Point resting=PointToScreen(f.FinalPosition);if(f.Location!=resting)f.Location=resting;}
            }
            float pulse=phase==3&&!muted&&!reducedMotion?audioClips["theme"].EnvelopeAt(now-phaseStarted):0;
            if(f.MusicPulse>0&&pulse==0&&f.ReactsToMusic)f.Invalidate();
            f.MusicPulse=pulse;
            bool corrupt=!reducedMotion&&now<f.CorruptUntil;
            if(f.Corrupted!=corrupt){f.Corrupted=corrupt;f.Invalidate();}
            f.VisualTime=reducedMotion?0:Math.Max(0,now-f.Birth)*(1+Escalation(now-phaseStarted));
            if((f.Corrupted||((f.Animated||f.ReactsToMusic)&&animated++<6))&&!reducedMotion&&alpha>0&&now-f.LastTexturePaint>=.066){f.LastTexturePaint=now;f.Invalidate();}
            if(Math.Abs(f.Alpha-alpha)>.001 || ((alpha==0 || alpha==1)&&f.Alpha!=alpha)){f.Alpha=alpha;f.Opacity=alpha;}
        }
    }
    void ClickCoin(object sender,MouseEventArgs e){
        if(e.Button!=MouseButtons.Left)return;
        if(phase==5){DismissVictory();return;}
        if(phase==0){StartGame();return;}
        if(phase!=3)return;
        if(Now()-phaseStarted>=90){SetPhase(4);return;}
        Rectangle panel=RansomPanel();
        if(new Rectangle(panel.X,panel.Y,panel.Width,24).Contains(e.Location)){
            draggingRansom=true;ransomDragOffset=new Point(e.X-panel.X,e.Y-panel.Y);Capture=true;return;
        }
        for(int i=coins.Count-1;i>=0;i--){
            Gold gold=coins[i];
            if(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)).Contains(e.Location)){
                double now=Now();
                combo=(now-lastCoinAt<=1.15)?Math.Min(5,combo+1):1;
                lastCoinAt=now;
                StartSound(gold.IsFake?"glitch":"gold",false,false);
                balance+=gold.Value;
                pickups.Add(new Pickup(new Point(gold.Position.X+CoinSize/2,gold.Position.Y+CoinSize/2),gold.Value,now,gold.Rare));
                foreach(CursorTrace trace in gold.Trail)Invalidate(new Rectangle(trace.Position,new Size(CoinSize,CoinSize)));
                coins.RemoveAt(i);
                if(balance>=500){balance=500;SetPhase(5);return;}
                coinRespawns.Add(now+2.5+random.NextDouble()*1.5);
                coinRespawnKinds.Add(gold.IsFake);
                respawnZones.Add(new Rectangle(gold.Position.X-7,gold.Position.Y-7,CoinSize+14,CoinSize+14));
                SpawnPopup(false,0);
                if(random.NextDouble()<.07)cursorUntil=now+.5;
                Invalidate(Rectangle.Inflate(new Rectangle(gold.Position,new Size(CoinSize,CoinSize)),8,8));
                Invalidate(RansomPanel());
                return;
            }
        }
    }
    void Center(Graphics g,string s,Font f,Brush b,float y){
        SizeF z=g.MeasureString(s,f);
        g.DrawString(s,f,b,(ClientSize.Width-z.Width)/2f,y);
    }
    static float CoinOpacity(double age,bool reduced){return reduced?1:(float)Ease(age/.28);}
    static void DrawAlpha(Graphics g,Bitmap bitmap,Rectangle destination,Rectangle source,float opacity){
        if(bitmap==null||opacity<=0)return;
        if(opacity>=.999f){g.DrawImage(bitmap,destination,source,GraphicsUnit.Pixel);return;}
        using(ImageAttributes alpha=new ImageAttributes()){
            ColorMatrix matrix=new ColorMatrix();matrix.Matrix33=opacity;alpha.SetColorMatrix(matrix);
            g.DrawImage(bitmap,destination,source.X,source.Y,source.Width,source.Height,GraphicsUnit.Pixel,alpha);
        }
    }
    void DrawGlitchCoin(Graphics g,Rectangle bounds,double now,float opacity){
        if(purpleCoin==null)return;
        DrawAlpha(g,purpleCoin,bounds,new Rectangle(0,0,CoinSize,CoinSize),opacity);
        if(reducedMotion)return;
        int tick=(int)(now*12);
        for(int i=0;i<3;i++){
            int y=12+(tick*13+i*19)%48;
            int offset=((tick+i)%2==0?1:-1)*(3+(tick+i)%4);
            Rectangle slice=new Rectangle(0,y,CoinSize,5);
            DrawAlpha(g,purpleCoin,new Rectangle(bounds.X+offset,bounds.Y+y,CoinSize,5),slice,opacity);
        }
    }
    void DrawRareCoin(Graphics g,Rectangle bounds,double now){DrawRareCoin(g,bounds,now,1);}
    void DrawRareCoin(Graphics g,Rectangle bounds,double now,float opacity){
        // A small layered contact shadow sits at the bag's base, not below the hitbox.
        float center=bounds.X+bounds.Width/2f;
        using(Brush outer=new SolidBrush(Color.FromArgb((int)(24*opacity),0,0,0)))g.FillEllipse(outer,center-28,bounds.Bottom-14,56,10);
        using(Brush middle=new SolidBrush(Color.FromArgb((int)(38*opacity),0,0,0)))g.FillEllipse(middle,center-23,bounds.Bottom-12,46,7);
        using(Brush contact=new SolidBrush(Color.FromArgb((int)(55*opacity),0,0,0)))g.FillEllipse(contact,center-17,bounds.Bottom-11,34,5);
        if(rareGold!=null)DrawAlpha(g,rareGold,bounds,new Rectangle(0,0,CoinSize,CoinSize),opacity);
    }
    void Card(Graphics g,Rectangle bounds,Color accent){
        using(Brush shadow=new SolidBrush(Color.FromArgb(90,0,0,0)))g.FillRectangle(shadow,bounds.X+5,bounds.Y+7,bounds.Width,bounds.Height);
        using(LinearGradientBrush fill=new LinearGradientBrush(bounds,Color.FromArgb(29,31,45),Color.FromArgb(13,15,23),75f))g.FillRectangle(fill,bounds);
        using(Pen edge=new Pen(Color.FromArgb(61,64,82)))g.DrawRectangle(edge,bounds);
        using(Brush stripe=new SolidBrush(accent))g.FillRectangle(stripe,bounds.X,bounds.Y,3,bounds.Height);
    }
    void DrawHud(Graphics g,Rectangle panel,double age){
        g.FillRectangle(Brushes.Red,panel);
        RetroTitle(g,panel,"RANSOM");
        using(Pen border=new Pen(Color.Silver,2))g.DrawRectangle(border,panel);
        if(face!=null){
            using(ImageAttributes tint=new ImageAttributes()){
                ColorMatrix matrix=new ColorMatrix(new float[][] {
                    new float[]{1.5f,1.5f,1.5f,0,0},new float[]{0,0,0,0,0},new float[]{0,0,0,0,0},
                    new float[]{0,0,0,1,0},new float[]{0,0,0,0,1}});
                tint.SetColorMatrix(matrix);
                g.DrawImage(face,new Rectangle(panel.X+15,panel.Y+34,112,112),0,0,face.Width,face.Height,GraphicsUnit.Pixel,tint);
            }
        }
        g.DrawString("YOUR FILES\nWILL BE\nENCRYPTED",warningFont,Brushes.White,new RectangleF(panel.X+140,panel.Y+34,panel.Width-150,111));
        Rectangle warning=new Rectangle(panel.X+11,panel.Y+153,panel.Width-22,100);
        g.FillRectangle(Brushes.Black,warning);g.DrawRectangle(Pens.White,warning);
        using(StringFormat centered=new StringFormat()){
            centered.Alignment=StringAlignment.Center;centered.LineAlignment=StringAlignment.Center;
            g.DrawString("IF YOU DO NOT PAY THIS RANSOM\nBEFORE THE TIMER ENDS, YOUR FILES\nWILL BE",ransomFont,Brushes.White,new RectangleF(warning.X,warning.Y+5,warning.Width,63),centered);
            g.DrawString("UNRECOVERABLE BY ANY MEANS.",ransomFont,Brushes.Red,new RectangleF(warning.X,warning.Y+68,warning.Width,24),centered);
        }
        Rectangle price=new Rectangle(panel.X+11,panel.Y+263,140,43);
        g.FillRectangle(Brushes.Black,price);g.DrawRectangle(Pens.White,price);
        int remaining=Math.Max(0,500-(int)Math.Round(displayedBalance));
        g.DrawString(remaining.ToString(),scoreFont,Brushes.Khaki,price.X+6,price.Y+3);
        if(coinImage!=null)g.DrawImage(coinImage,new Rectangle(price.Right-40,price.Y+5,34,34));
        Rectangle countdown=new Rectangle(panel.X+160,panel.Y+263,panel.Width-171,43);
        g.DrawRectangle(Pens.White,countdown);
        int left=Math.Max(0,(int)Math.Ceiling(90-age));
        g.DrawString("TIME:",scoreFont,Brushes.Black,countdown.X+5,countdown.Y+3);
        using(StringFormat right=new StringFormat()){
            right.Alignment=StringAlignment.Far;
            g.DrawString(CountdownVisual(left,!reducedMotion&&Now()<timerGlitchUntil,Now()),scoreFont,Brushes.Black,new RectangleF(countdown.X,countdown.Y+3,countdown.Width-8,40),right);
        }
    }
    static void RetroTitle(Graphics g,Rectangle window,string title,int titleWidth=-1){
        Rectangle bar=new Rectangle(window.X+1,window.Y+1,window.Width-2,23);
        using(LinearGradientBrush chrome=new LinearGradientBrush(bar,Color.FromArgb(234,234,239),Color.FromArgb(95,95,103),90f)){
            chrome.InterpolationColors=new ColorBlend { Colors=new Color[]{Color.FromArgb(239,239,245),Color.FromArgb(164,164,173),Color.FromArgb(91,91,100)},Positions=new float[]{0,.42f,1} };
            g.FillRectangle(chrome,bar);
        }
        g.DrawLine(Pens.White,bar.Left,bar.Top,bar.Right,bar.Top);
        using(Font label=new Font("Tahoma",8))using(StringFormat format=new StringFormat()){
            format.FormatFlags=StringFormatFlags.NoWrap;format.Trimming=StringTrimming.EllipsisCharacter;
            g.DrawString(title,label,Brushes.White,new RectangleF(bar.X+5,bar.Y+4,Math.Max(1,titleWidth<0?bar.Width-10:titleWidth),18),format);
        }
    }
    static Point DownloadShake(double time,double progress,bool reduced){
        if(reduced)return Point.Empty;
        double strength=.7+.3*Math.Max(0,Math.Min(1,progress));
        return new Point((int)((Math.Sin(time*104)*16+Math.Sin(time*187)*8)*strength),
                         (int)((Math.Sin(time*131)*12+Math.Cos(time*211)*5)*strength));
    }
    static string DownloadLabel(double progress){
        return "DOWNLOADING"+new string('.',(int)(Math.Max(0,Math.Min(1,progress))*10));
    }
    void DrawPickups(Graphics g,double now){
        foreach(Pickup effect in pickups){
            double t=Math.Min(1,(now-effect.Birth)/.7);
            int alpha=(int)(255*(1-t));
            Color color=effect.Value<=0?Color.FromArgb(alpha,255,70,100):effect.Rare?Color.FromArgb(alpha,255,222,125):Color.FromArgb(alpha,166,248,209);
            float radius=(float)(14+Ease(t)*44);
            if(!reducedMotion)using(Pen ring=new Pen(Color.FromArgb(alpha/2,color),2))g.DrawEllipse(ring,effect.Position.X-radius,effect.Position.Y-radius,radius*2,radius*2);
            using(Brush ink=new SolidBrush(color))using(StringFormat center=new StringFormat()){
                center.Alignment=StringAlignment.Center;
                g.DrawString(effect.Value==0?"GLITCH":effect.Value<0?effect.Value.ToString():"+"+effect.Value,uiFont,ink,effect.Position.X,effect.Position.Y-(reducedMotion?18:(float)(Ease(t)*48)),center);
            }
        }
    }
    static Point JumpscareOffset(double age,double duration,bool reduced){
        if(reduced||age<0||age>=duration)return Point.Empty;
        double strength=8+5*Math.Exp(-age*5);
        return new Point((int)(Math.Sin(age*97)*strength),(int)(Math.Sin(age*123+.6)*strength*.7));
    }
    void DrawCloseCall(Graphics g,double age){
        int cx=ClientSize.Width/2,cy=ClientSize.Height/2;
        Rectangle card=new Rectangle(cx-310,cy-255,620,510);
        Card(g,card,Color.Gold);
        Center(g,"THAT WAS CLOSE!",headline,Brushes.Gold,cy-225);
        Center(g,"ESCAPED WITH "+secondsAtWin.ToString("0.0")+" SECONDS LEFT",small,Brushes.White,cy-175);
        double reveal=reducedMotion?1:Ease(age/.65);
        if(victory!=null){
            int width=(int)(470*(.75+.25*reveal));
            int height=(int)(width*(double)victory.Height/victory.Width);
            g.DrawImage(victory,new Rectangle(cx-width/2,cy-height/2+10,width,height));
        }
        if(!reducedMotion&&age<2){
            for(int i=0;i<20;i++){
                double direction=i*Math.PI*2/20;
                double travel=Ease(age/2)*210;
                float x=cx+(float)(Math.Cos(direction)*travel),y=cy+(float)(Math.Sin(direction)*travel*.65);
                g.FillRectangle(i%2==0?Brushes.Gold:Brushes.LimeGreen,x,y,4,7);
            }
        }
        Center(g,age<SoundDuration("success")?"THANK YOU FOR PLAYING":"ENTER / CLICK TO EXIT",caption,Brushes.White,cy+215);
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);
        Graphics g=e.Graphics;
        g.SmoothingMode=SmoothingMode.HighQuality;
        g.InterpolationMode=InterpolationMode.HighQualityBilinear;
        if(phase==3)g.DrawString("M: SOUND "+(muted?"OFF":"ON")+"   R: MOTION "+(reducedMotion?"REDUCED":"FULL")+"   ESC: EXIT",caption,Brushes.White,12,8);
        int w=ClientSize.Width,h=ClientSize.Height,cx=w/2,cy=h/2;
        double age=Now()-phaseStarted;
        if(phase==0){
            g.Clear(Color.FromArgb(36,0,0));
            if(age<IntroFaceDuration&&face!=null){
                g.DrawImage(face,new Rectangle(cx-90,cy-90,180,180));
            } else if(stopSign!=null){
                g.InterpolationMode=InterpolationMode.NearestNeighbor;
                g.DrawImage(stopSign,new Rectangle(cx-90,cy-90,180,180));
                StartStopSignSound();
            }
        } else if(phase==1){
            g.Clear(Color.FromArgb(135,0,0));
            float impact=reducedMotion?1:(float)(1+.14*(1-Ease(age/.23)));
            int side=(int)(405*impact);
            Point movement=JumpscareOffset(age,AttackFaceDuration,reducedMotion);
            if(jumpscare!=null)g.DrawImage(jumpscare,new Rectangle(cx-side/2+movement.X,cy-side/2-25+movement.Y,side,side));
        } else if(phase==2){
            g.Clear(Color.FromArgb(135,0,0));
            double progress=Math.Max(0,Math.Min(1,(Now()-attackStarted-AttackFaceDuration)/(AttackSlashTime-AttackFaceDuration)));
            GraphicsState downloadState=g.Save();
            Point shake=DownloadShake(Now()-attackStarted,progress,reducedMotion);
            g.TranslateTransform(cx+shake.X,cy+shake.Y);
            if(!reducedMotion)g.RotateTransform((float)(Math.Sin((Now()-attackStarted)*93)*1.4));
            g.TranslateTransform(-cx,-cy);
            int barWidth=Math.Min(850,(int)(w*.62));
            Rectangle track=new Rectangle(cx-barWidth/2,cy+10,barWidth,22);
            Center(g,DownloadLabel(progress),downloadFont,Brushes.White,track.Y-29);
            g.FillRectangle(Brushes.Black,track);
            using(Pen border=new Pen(Color.FromArgb(181,48,61)))g.DrawRectangle(border,track);
            Rectangle interior=Rectangle.Inflate(track,-2,-2);
            float step=interior.Width/10f;
            float filled=interior.Width*(float)progress;
            for(int i=0;i<10;i++){
                float left=i*step;
                float width=Math.Min(step-2,filled-left);
                if(width<=0)break;
                RectangleF segment=new RectangleF(interior.X+left,interior.Y,width,interior.Height);
                using(LinearGradientBrush red=new LinearGradientBrush(interior,Color.FromArgb(225,36,65),Color.FromArgb(143,0,21),90f))g.FillRectangle(red,segment);
                using(Pen shine=new Pen(Color.FromArgb(245,68,90)))g.DrawLine(shine,segment.Left,segment.Top+1,segment.Right,segment.Top+1);
            }
            g.Restore(downloadState);
        } else if(phase==3){
            // Cosmetic desktop pixel corruption only. No OS icons are moved or modified.
            if(!reducedMotion&&((int)(Now()*7)%5)<2){
                Random glitch=new Random((int)(Now()*8));
                for(int i=0;i<12;i++){
                    int x=glitch.Next(12,Math.Max(13,w/5)),y=glitch.Next(90,Math.Max(91,h-110));
                    g.FillRectangle(i%2==0?Brushes.Red:Brushes.Black,x,y,glitch.Next(12,43),glitch.Next(2,8));
                }
                g.DrawString("????.lnk",caption,Brushes.Red,35,55);
            }
            Rectangle panel=RansomPanel();
            if(e.ClipRectangle.IntersectsWith(panel))DrawHud(g,panel,age);
            foreach(Gold coin in coins){
                foreach(CursorTrace trace in coin.Trail){
                    float strength=(float)(.18*Math.Max(0,1-(Now()-trace.Birth)/.3));
                    Bitmap art=coin.IsFake?purpleCoin:coinImage;
                    if(art!=null)DrawAlpha(g,art,new Rectangle(trace.Position.X+15,trace.Position.Y+15,46,46),new Rectangle(0,0,CoinSize,CoinSize),strength);
                }
                Rectangle r=new Rectangle(coin.Position.X,coin.Position.Y,CoinSize,CoinSize);
                if(!e.ClipRectangle.IntersectsWith(Rectangle.Inflate(r,8,8)))continue;
                Point pointer=PointToClient(Cursor.Position);
                float opacity=CoinOpacity(Now()-coin.Birth,reducedMotion);
                if(opacity>.95f&&r.Contains(pointer))using(Pen hover=new Pen(Color.FromArgb(160,255,231,165),2))g.DrawEllipse(hover,r);
                if(coin.Rare)DrawRareCoin(g,r,Now(),opacity);
                else {
                    using(Brush shadow=new SolidBrush(Color.FromArgb((int)(70*opacity),0,0,0)))g.FillEllipse(shadow,r.X+9,r.Bottom-8,r.Width-18,12);
                    if(coin.IsFake)DrawGlitchCoin(g,r,Now(),opacity);
                    else if(coinImage!=null)DrawAlpha(g,coinImage,r,new Rectangle(0,0,CoinSize,CoinSize),opacity);
                    else g.FillEllipse(Brushes.Gold,r);
                }
            }
            DrawPickups(g,Now());
            DrawCoinBursts(g,Now());
            foreach(CursorTrace trace in cursorTrail){
                int alpha=(int)(160*Math.Max(0,1-(Now()-trace.Birth)/.28));
                using(Brush ink=new SolidBrush(Color.FromArgb(alpha,255,30,100)))g.FillRectangle(ink,trace.Position.X+4,trace.Position.Y+4,7,3);
                using(Brush ink=new SolidBrush(Color.FromArgb(alpha,60,200,255)))g.FillRectangle(ink,trace.Position.X-3,trace.Position.Y+9,4,5);
            }
            // Cosmetic red hand near the cursor. The actual Windows pointer is unchanged.
            if(!reducedMotion&&Now()<cursorUntil){
                Point p=PointToClient(Cursor.Position);
                using(Pen red=new Pen(Color.Red,4)){
                    g.DrawLine(red,p.X+12,p.Y+15,p.X+12,p.Y-6);
                    g.DrawLine(red,p.X+12,p.Y+4,p.X+27,p.Y-1);
                    g.DrawLine(red,p.X+27,p.Y-1,p.X+30,p.Y+24);
                    g.DrawLine(red,p.X+30,p.Y+24,p.X+15,p.Y+34);
                    g.DrawLine(red,p.X+15,p.Y+34,p.X+2,p.Y+16);
                }
            }
        } else if(phase==4){
            if(failureView==null&&age<SoundDuration("failure")){
                Point movement=JumpscareOffset(age,SoundDuration("failure"),reducedMotion);
                if(jumpscare!=null)g.DrawImage(jumpscare,new Rectangle(cx-220+movement.X,cy-220+movement.Y,440,440));
                }
        } else if(phase==5){
            if(closeCallWin){DrawCloseCall(g,age);return;}
            // Wait for popups to finish fading out, then show the exact thank-you art.
            if(age>.48){
                if(victory!=null){
                    int imgW=Math.Min(527,w-16);int imgH=(int)(imgW*(double)victory.Height/victory.Width);
                    g.DrawImage(victory,new Rectangle(cx-imgW/2,cy-imgH/2,imgW,imgH));
                } else {
                    g.FillRectangle(Brushes.LimeGreen,cx-263,cy-184,527,369);
                    Center(g,"THANK YOU",headline,Brushes.White,cy-115);
                    Center(g,"LIKE!",headline,Brushes.White,cy+10);
                }
                Center(g,age<SoundDuration("success")?"THANK YOU FOR PLAYING  |  ESC TO EXIT":"ENTER / CLICK TO EXIT",caption,Brushes.White,cy+205);
            }
        }
    }
    protected override void Dispose(bool disposing){
        if(disposing&&!resourcesReleased){
            resourcesReleased=true;
            if(timer!=null)timer.Dispose();
            ClearMonitorTints();
            if(failureView!=null)failureView.Dispose();
            if(corruptionWave!=null)corruptionWave.Dispose();
            if(preciseTimer){timeEndPeriod(1);preciseTimer=false;}
            foreach(FakePopup popup in popups)if(!popup.IsDisposed)popup.Dispose();
            foreach(AudioClip clip in audioClips.Values)clip.Dispose();
            if(applicationIcon!=null)applicationIcon.Dispose();
            uiFont.Dispose();scoreFont.Dispose();warningFont.Dispose();ransomFont.Dispose();
            if(face!=null)face.Dispose();if(coinImage!=null)coinImage.Dispose();if(victory!=null)victory.Dispose();if(stopSign!=null)stopSign.Dispose();if(jumpscare!=null)jumpscare.Dispose();if(popupFace!=null)popupFace.Dispose();if(rareGold!=null)rareGold.Dispose();if(purpleCoin!=null)purpleCoin.Dispose();
            headline.Dispose();small.Dispose();caption.Dispose();downloadFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
