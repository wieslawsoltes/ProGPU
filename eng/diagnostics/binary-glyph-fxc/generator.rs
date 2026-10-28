use std::{env,fs};
use naga::{ResourceBinding, back::hlsl, valid::{Validator,ValidationFlags,Capabilities}};
fn main() {
    let args:Vec<_>=env::args().collect();
    assert_eq!(args.len(),3,"input.wgsl output.hlsl");
    let source=fs::read_to_string(&args[1]).unwrap();
    let module=naga::front::wgsl::parse_str(&source).unwrap();
    let info=Validator::new(ValidationFlags::all(),Capabilities::all()).validate(&module).unwrap();
    let mut options=hlsl::Options::default();
    options.shader_model=hlsl::ShaderModel::V5_1;
    options.fake_missing_bindings=false;
    options.special_constants_binding=Some(hlsl::BindTarget{space:0,register:1,binding_array_size:None});
    for (binding,register) in [(0,0),(1,0),(2,1),(3,0),(4,1)] {
        options.binding_map.insert(ResourceBinding{group:0,binding},hlsl::BindTarget{space:0,register,binding_array_size:None});
    }
    let mut output=String::new();
    let reflection=hlsl::Writer::new(&mut output,&options).write(&module,&info).unwrap();
    assert_eq!(reflection.entry_point_names.len(),2);
    for (ep,name) in module.entry_points.iter().zip(reflection.entry_point_names.iter()) {
        let name=name.as_ref().unwrap();
        assert_eq!(ep.name,*name);
        println!("entrypoint={name}");
    }
    fs::write(&args[2],output).unwrap();
}
