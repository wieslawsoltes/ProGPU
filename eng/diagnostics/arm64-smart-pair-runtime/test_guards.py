import ast,copy,hashlib,pathlib,unittest
base=pathlib.Path(__file__).parent
# Load only pure admission functions, without platform/environment side effects.
tree=ast.parse((base/'stage.py').read_text());scope={'hashlib':hashlib,'re':__import__('re'),'SHADER':'e48c9e0428888a560902600c0e625684c13b600f534215b309dc2f24b6159707','NAMES':{'progpu_native.dll','progpu_native_dawn.dll'}}
for node in tree.body:
 if isinstance(node,ast.FunctionDef) and node.name in ('validate_embedded','validate_overlay'):exec(compile(ast.Module([node],[]),'guards','exec'),scope)
class Controls(unittest.TestCase):
 def setUp(self):
  self.canonical=(base/'candidate.wgsl').read_bytes();self.before={'managed.dll':'same','wgpu_native.dll':'same','progpu_native.dll':'old','progpu_native_dawn.dll':'old'};self.after=self.before|{'progpu_native.dll':'new','progpu_native_dawn.dll':'new'}
 def test_candidate_lf(self):scope['validate_embedded'](self.canonical,b'original source',b'prefix'+self.canonical+b'\0suffix')
 def test_exact_git_crlf(self):scope['validate_embedded'](self.canonical,b'original source',self.canonical.replace(b'\n',b'\r\n')+b'\0')
 def test_other_source_rejected(self):self.assertRaises(AssertionError,scope['validate_embedded'],self.canonical,b'original source',self.canonical+b' '+b'\0')
 def test_stale_shader_rejected(self):self.assertRaises(AssertionError,scope['validate_embedded'],self.canonical,b'original source',b'original source\0')
 def test_missing_terminator_rejected(self):self.assertRaises(AssertionError,scope['validate_embedded'],self.canonical,b'original source',self.canonical)
 def test_duplicate_shader_rejected(self):self.assertRaises(AssertionError,scope['validate_embedded'],self.canonical,b'original source',(self.canonical+b'\0')*2)
 def test_candidate_plus_original_rejected(self):self.assertRaises(AssertionError,scope['validate_embedded'],self.canonical,b'original source',self.canonical+b'\0original source\0')
 def test_overlay_admitted(self):self.assertEqual(scope['validate_overlay'](self.before,self.after),scope['NAMES'])
 def test_dependency_change_rejected(self):self.assertRaises(AssertionError,scope['validate_overlay'],self.before,self.after|{'wgpu_native.dll':'wrong'})
 def test_managed_change_rejected(self):self.assertRaises(AssertionError,scope['validate_overlay'],self.before,self.after|{'managed.dll':'wrong'})
 def test_extra_file_rejected(self):self.assertRaises(AssertionError,scope['validate_overlay'],self.before,self.after|{'extra.dll':'wrong'})
 def test_missing_file_rejected(self):
  del self.after['managed.dll'];self.assertRaises(AssertionError,scope['validate_overlay'],self.before,self.after)
 def test_stale_candidate_rejected(self):self.assertRaises(AssertionError,scope['validate_overlay'],self.before,self.before)
if __name__=='__main__':unittest.main()
