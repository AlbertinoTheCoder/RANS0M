"""Prepare runtime PCM audio from the editable sources. Requires FFmpeg."""
from pathlib import Path
import subprocess
root=Path(__file__).resolve().parent
output=root/'assets'/'audio'
output.mkdir(exist_ok=True)
tracks={
    'anticipation': ('source/anticipation.ogg',0.5,1.72),
    'attack': ('source/attack.ogg',0,4.4),
    'failure': ('source/failure.mp3',0.165,None),
    'success': ('source/success.ogg',0,None),
    'spawn': ('source/GoldSpawn.wav',0,None),
    'glitch': ('source/Fragment_Glitch.mpeg',0,1),
    'theme': ('theme.mp3',34.4,90),
}
for name,(source,start,duration) in tracks.items():
    cmd=['ffmpeg','-v','error','-y','-i',str(root/'assets'/source),'-ss',str(start)]
    if duration is not None: cmd+=['-t',str(duration)]
    if name=='glitch': cmd+=['-af','volume=0.25,afade=t=out:st=0.985:d=0.015']
    if name=='anticipation': cmd+=['-af','afade=t=in:st=0:d=0.003,afade=t=out:st=1.705:d=0.015']
    cmd+=['-c:a','pcm_s16le','-ar','44100','-ac','2',str(output/(name+'.wav'))]
    subprocess.run(cmd,check=True)
    print('Prepared',name)

# Both pickup recordings start together in one predecoded voice.
subprocess.run(['ffmpeg','-v','error','-y','-i',str(root/'assets/source/GoldIncrease.mp3'),'-i',str(root/'assets/source/GoldPickup.wav'),'-filter_complex','[0:a][1:a]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:level=0','-c:a','pcm_s16le','-ar','44100','-ac','2',str(output/'gold.wav')],check=True)
