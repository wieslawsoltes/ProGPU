"""Original capture admission; differences remain diagnostic observations."""
import hashlib,json,math,pathlib

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def validate(root, prefix, architecture="X64"):
    data=json.loads((root/'reference.json').read_text())
    assert data['schema']=='original-gdi-text-raster-v1' and data['qualified'] is False
    assert data['inputEvents']==0 and data['desktopWindows']==0
    assert (data['text'],data['width'],data['height'],data['dpi'])==('Name Age 123 Hello Windows Forms',384,64,96)
    assert isinstance(data['elapsedSeconds'],(float,int)) and math.isfinite(data['elapsedSeconds']) and 0<=data['elapsedSeconds']<=45
    cases=data['cases'];assert len(cases)==72
    original=json.loads((pathlib.Path(__file__).resolve().parent/'original-contract.json').read_text())
    assert [{k:f[k] for k in original['fonts'][0]} for f in data['fonts']]==original['fonts']
    assert data['smoothing']==original['smoothing']
    assert [{k:c[k] for k in original['caseInputs'][0]} for c in cases]==original['caseInputs']
    expected=[]
    for name,points in [('Microsoft Sans Serif',9.75),('Segoe UI',9)]:
        for hint in ['SystemDefault','SingleBitPerPixelGridFit','SingleBitPerPixel','AntiAliasGridFit','AntiAlias','ClearTypeGridFit']:
            for contrast in [0,4,12]:
                for api in ['GdiPlusDrawString','GdiTextRenderer']:expected.append((name,points,hint,contrast,api))
    assert [(c['font'],c['points'],c['requestedHint'],c['requestedContrast'],c['api']) for c in cases]==expected
    driver=data.get('driverGlyphs',[]);assert len(driver)==(384 if prefix else 0)
    if prefix:
        assert all('outlines' in f and 'bitmaps' in f for f in data['fonts'])
    else:assert all('outlines' not in f and 'bitmaps' not in f for f in data['fonts'])
    assert len(data['fonts'])==2
    names=set()
    for c in cases+driver:
        image=c['image'];name=image['name'];assert pathlib.PurePosixPath(name).name==name and '\\' not in name and name.endswith('.png') and name not in names;names.add(name)
        p=root/name;assert p.is_file() and not p.is_symlink() and p.stat().st_size==image['bytes'] and sha(p)==image['sha256']
        assert len(c['rgbaSha256'])==64 and c['rgbaSha256']==c['rgbaSha256'].lower()
        int(c['rgbaSha256'],16)
    assert {p.name for p in root.iterdir()}==names|{'reference.json'}
    assert sum((root/n).stat().st_size for n in names)==data['pngBytes']<=16*1024*1024
    identity=data['loadedIdentity'];assert identity['architecture']==architecture
    assert len(identity['native'])==2 and len(identity['managed'])==3
    assert {pathlib.PureWindowsPath(v['path']).name.lower() for v in identity['native']}=={'gdi32.dll','gdiplus.dll'}
    for item in identity['managed']+identity['native']:
        assert item['bytes']>0 and len(item['sha256'])==64 and item['version']
    return data

def compare(a,b):
    # Prefix observation metadata is deliberately additional, never a new font.
    common_font_keys=('requested','managed','native','points','charset','bytes','sha256','gasp')
    assert [{k:f[k] for k in common_font_keys} for f in a['fonts']]==[{k:f[k] for k in common_font_keys} for f in b['fonts']]
    assert a['smoothing']==b['smoothing']
    def identity(d):
        v=dict(d['loadedIdentity']);v['native']=sorted(v['native'],key=lambda m:m['path'].lower());return v
    assert identity(a)==identity(b)
    changed=[]
    for index,(left,right) in enumerate(zip(a['cases'],b['cases'],strict=True)):
        for key in ('font','points','api','requestedHint','requestedContrast','actual'):assert left[key]==right[key]
        if left['rgbaSha256']!=right['rgbaSha256']:
            changed.append({'index':index,'font':left['font'],'api':left['api'],'hint':left['requestedHint'],'contrast':left['requestedContrast'],'withoutPrefix':left['rgbaSha256'],'withPrefix':right['rgbaSha256']})
    return {'qualified':False,'diagnosticOnly':True,'sameActualHostRuntimeNativeModulesFontsSettings':True,'comparedFrames':72,'differentFrameCount':len(changed),'differentFrames':changed,'baselineSubstitution':False}
