import copy,importlib.util,json,pathlib,tempfile,unittest,zipfile,hashlib
from contract import compare,validate
ROOT=pathlib.Path(__file__).resolve().parent
ARCHIVE=pathlib.Path('/Users/wieslawsoltes/.codex/visualizations/2026/10/28/forms174-preparation/original-glyphs-39141668302.zip')

class OriginalControls(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=pathlib.Path(self.temp.name)
        with zipfile.ZipFile(ARCHIVE) as z:
            name=next(n for n in z.namelist() if n.endswith('/original/reference.json'))
            self.original=json.loads(z.read(name));prefix=name.removesuffix('reference.json')
            for case in self.original['cases']+self.original['driverGlyphs']:
                image=case['image']['name'];(self.root/image).write_bytes(z.read(prefix+image))
        # Authored identity shape only; original historical process has no module
        # witness. This fixture never becomes a replacement reference/result.
        self.original['loadedIdentity']={'architecture':'X64','managed':[{'path':f'C:/framework/{i}.dll','bytes':1,'sha256':'a'*64,'version':'1'} for i in range(3)],'native':[{'path':'C:/Windows/'+n,'bytes':1,'sha256':'b'*64,'version':'1'} for n in ['gdi32.dll','gdiplus.dll']]}
    def write(self,data):
        (self.root/'reference.json').write_text(json.dumps(data));return self.root
    def test_original_inventory_passes(self):validate(self.write(self.original),True)
    def test_missing_image_rejected(self):
        (self.root/self.original['cases'][0]['image']['name']).unlink()
        with self.assertRaises(AssertionError):validate(self.write(self.original),True)
    def test_changed_image_rejected(self):
        (self.root/self.original['cases'][0]['image']['name']).write_bytes(b'changed')
        with self.assertRaises(AssertionError):validate(self.write(self.original),True)
    def test_incomplete_prefix_rejected(self):
        self.original['driverGlyphs'].pop()
        with self.assertRaises(AssertionError):validate(self.write(self.original),True)
    def test_missing_native_identity_rejected(self):
        self.original['loadedIdentity']['native'].pop()
        with self.assertRaises(AssertionError):validate(self.write(self.original),True)
    def test_same_case_outcomes_are_reported(self):
        self.assertEqual(compare(self.original,copy.deepcopy(self.original))['differentFrameCount'],0)
    def test_pixel_difference_is_retained_diagnostic(self):
        changed=copy.deepcopy(self.original);changed['cases'][2]['rgbaSha256']='c'*64
        result=compare(self.original,changed);self.assertEqual(result['differentFrameCount'],1);self.assertFalse(result['qualified']);self.assertFalse(result['baselineSubstitution'])
    def test_font_settings_module_drift_rejected(self):
        for kind in ['font','settings','module']:
            changed=copy.deepcopy(self.original)
            if kind=='font':changed['fonts'][0]['sha256']='d'*64
            elif kind=='settings':changed['smoothing'][0]['value']=99
            else:changed['loadedIdentity']['native'][0]['sha256']='e'*64
            with self.subTest(kind=kind),self.assertRaises(AssertionError):compare(self.original,changed)
    def test_common_drift_from_original_inputs_is_rejected(self):
        for kind in ['font','settings','state','deadline']:
            changed=copy.deepcopy(self.original)
            if kind=='font':changed['fonts'][0]['sha256']='d'*64
            elif kind=='settings':changed['smoothing'][0]['value']=99
            elif kind=='state':changed['cases'][0]['actual']['dpiX']=120
            else:changed['elapsedSeconds']=45.01
            with self.subTest(kind=kind),self.assertRaises(AssertionError):validate(self.write(changed),True)

    def test_source72_loop_and_all_historical_helpers_unchanged(self):
        receipt=json.loads((ROOT/'original-source-receipt.json').read_text())
        loops=[]
        for variant in ['without-prefix','with-prefix']:
            s=(ROOT/variant/'Program.cs').read_text()
            start=s.index('            foreach (TextRenderingHint hint in Enum.GetValues<TextRenderingHint>())')
            end=s.index('        Budget();',start)
            loops.append(s[start:end])
            self.assertIn('clock.Elapsed.TotalSeconds > 45',s)
            self.assertIn('bytes > 16 * 1024 * 1024',s)
        self.assertEqual(loops[0],loops[1])
        for entry in receipt:
            data=(ROOT/entry['variant']/entry['name']).read_bytes()
            if entry['name']=='Program.cs':data=data.replace(b'            loadedIdentity = OriginalModules.Capture(),\r\n',b'')
            self.assertEqual(hashlib.sha256(data).hexdigest(),entry['originalSha256'])

if __name__=='__main__':unittest.main()
