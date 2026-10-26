using System;
using System.IO;
using System.Runtime.InteropServices;

// PCM buffers and device headers are prepared once, before the first frame.
// Each clip has a device voice, allowing music and effects to overlap.
sealed class AudioClip : IDisposable {
    [StructLayout(LayoutKind.Sequential, Pack=2)]
    struct WaveFormat {
        public ushort format, channels;
        public uint rate, bytesPerSecond;
        public ushort blockAlign, bits, extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader {
        public IntPtr data;
        public uint length, recorded;
        public UIntPtr user;
        public uint flags, loops;
        public IntPtr next;
        public UIntPtr reserved;
    }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr handle,uint device,ref WaveFormat format,IntPtr callback,IntPtr user,uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr handle,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr handle);
    IntPtr device, data, header;
    readonly uint headerSize=(uint)Marshal.SizeOf(typeof(WaveHeader));
    bool prepared, active;
    uint pcmLength, alignment;
    float[] envelope;
    public double Duration { get; private set; }
    public float EnvelopeAt(double seconds){if(envelope==null||seconds<0||seconds>=Duration)return 0;return envelope[Math.Min(envelope.Length-1,(int)(seconds/.02))];}
    public bool Ready { get { return device!=IntPtr.Zero && prepared; } }

    public AudioClip(Stream stream){
        if(stream==null)throw new InvalidDataException("Embedded audio is missing.");
        byte[] pcm=null;
        WaveFormat format=new WaveFormat();
        using(stream)using(BinaryReader reader=new BinaryReader(stream)){
            if(new string(reader.ReadChars(4))!="RIFF")throw new InvalidDataException("Invalid WAV header.");
            reader.ReadUInt32();
            if(new string(reader.ReadChars(4))!="WAVE")throw new InvalidDataException("Invalid WAV format.");
            while(stream.Position+8<=stream.Length){
                string chunk=new string(reader.ReadChars(4));
                uint length=reader.ReadUInt32();
                long end=stream.Position+length;
                if(end>stream.Length)throw new InvalidDataException("Truncated WAV chunk.");
                if(chunk=="fmt "){
                    if(length<16)throw new InvalidDataException("Truncated PCM format chunk.");
                    format.format=reader.ReadUInt16();format.channels=reader.ReadUInt16();
                    format.rate=reader.ReadUInt32();format.bytesPerSecond=reader.ReadUInt32();
                    format.blockAlign=reader.ReadUInt16();format.bits=reader.ReadUInt16();
                } else if(chunk=="data")pcm=reader.ReadBytes(checked((int)length));
                stream.Position=Math.Min(stream.Length,end+(length%2));
            }
        }
        if(format.format!=1||format.bits!=16||(format.channels!=1&&format.channels!=2)||format.rate==0||format.blockAlign!=format.channels*2||format.bytesPerSecond!=format.rate*format.blockAlign||pcm==null||pcm.Length==0||pcm.Length%format.blockAlign!=0)
            throw new InvalidDataException("Expected 16-bit PCM audio.");
        Duration=(double)pcm.Length/format.bytesPerSecond;
        envelope=new float[(int)Math.Ceiling(Duration/.02)];
        int window=Math.Max(2,(int)(format.bytesPerSecond*.02));window-=window%format.blockAlign;
        for(int frame=0;frame<envelope.Length;frame++){
            double energy=0;int count=0;
            for(int sample=frame*window;sample+1<Math.Min(pcm.Length,(frame+1)*window);sample+=8){short value=(short)(pcm[sample]|pcm[sample+1]<<8);energy+=(double)value*value;count++;}
            envelope[frame]=count==0?0:(float)Math.Min(1,Math.Sqrt(energy/count)/32768*3);
        }
        pcmLength=(uint)pcm.Length;alignment=format.blockAlign;
        // Audio-device failure leaves the game playable with sound disabled.
        if(waveOutOpen(out device,uint.MaxValue,ref format,IntPtr.Zero,IntPtr.Zero,0)!=0){device=IntPtr.Zero;return;}
        try {
            data=Marshal.AllocHGlobal(pcm.Length);Marshal.Copy(pcm,0,data,pcm.Length);
            header=Marshal.AllocHGlobal((int)headerSize);
            WaveHeader value=new WaveHeader();value.data=data;value.length=(uint)pcm.Length;
            Marshal.StructureToPtr(value,header,false);
            if(waveOutPrepareHeader(device,header,headerSize)!=0)throw new InvalidOperationException("Audio buffer preparation failed.");
            prepared=true;
        } catch { Dispose();throw; }
    }
    public void Play(bool loop){PlayAt(loop,0);}
    public void PlayFrom(double seconds){PlayAt(false,Math.Max(0,seconds));}
    void PlayAt(bool loop,double seconds){
        if(!Ready)return;
        Stop();
        WaveHeader value=(WaveHeader)Marshal.PtrToStructure(header,typeof(WaveHeader));
        uint offset=(uint)Math.Min(pcmLength,seconds/Duration*pcmLength);
        offset-=offset%alignment;
        if(offset>=pcmLength)return;
        value.data=IntPtr.Add(data,(int)offset);value.length=pcmLength-offset;
        value.flags=2u|(loop?12u:0u); // PREPARED, BEGINLOOP, ENDLOOP
        value.loops=loop?uint.MaxValue:0;
        Marshal.StructureToPtr(value,header,false);
        active=waveOutWrite(device,header,headerSize)==0;
    }
    public void Stop(){if(device!=IntPtr.Zero&&active){waveOutReset(device);active=false;}}
    public void Dispose(){
        if(device!=IntPtr.Zero){
            Stop();
            if(prepared)waveOutUnprepareHeader(device,header,headerSize);
            waveOutClose(device);device=IntPtr.Zero;prepared=false;
        }
        if(header!=IntPtr.Zero){Marshal.FreeHGlobal(header);header=IntPtr.Zero;}
        if(data!=IntPtr.Zero){Marshal.FreeHGlobal(data);data=IntPtr.Zero;}
    }
}
