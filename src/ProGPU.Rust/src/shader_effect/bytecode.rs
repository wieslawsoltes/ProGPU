//! Original bounded D3D9 pixel-shader decoder and WGSL-body emission.
use crate::Error;
const LANES: [char; 4] = ['x', 'y', 'z', 'w'];
const SINCOS: [[f32; 4]; 2] = [
    [-1.5500992e-6, -2.1701389e-5, 0.0026041667, 0.00026041668],
    [-0.020833334, -0.125, 1.0, 0.5],
];
fn register(token: u32) -> u32 {
    ((token >> 28) & 7) | ((token >> 8) & 24)
}
fn parameter(token: u32) -> bool {
    token & 0x8000_0000 != 0 && token & 0xe000 == 0
}
fn modified(mut expression: String, modifier: u32) -> String {
    if matches!(modifier, 11 | 12) {
        expression = format!("abs({expression})");
    }
    if matches!(modifier, 1 | 12) {
        expression = format!("(-{expression})");
    }
    expression
}
/// Validate and translate the complete original bounded ps_2_0/ps_3_0 stream.
/// Output is published only after exact END, complete color writes and a sample.
/// Unsupported numeric domains and resource forms reject without substitution.
pub fn translate(bytecode: &[u8], source_sampler: u32) -> Result<String, Error> {
    if !(8..=65536).contains(&bytecode.len()) || bytecode.len() % 4 != 0 || source_sampler >= 16 {
        return Err(Error::InvalidInput);
    }
    let word = |i: usize| u32::from_le_bytes(bytecode[i * 4..i * 4 + 4].try_into().unwrap());
    let model3 = word(0) == 0xffff0300;
    if !model3 && word(0) != 0xffff0200 {
        return Err(Error::InvalidInput);
    }
    let mut body = String::new();
    let mut written = [0u32; 12];
    let mut definitions = [false; 32];
    let mut definition_bits = [[0u32; 4]; 32];
    let (mut output_written, mut instructions, mut input_mask, mut input_register) =
        (0u32, 0u32, 0u32, 0u32);
    let (mut sampler_declared, mut executable, mut sampled) = (false, false, false);
    let mut cursor = 1;
    macro_rules! reject {
        ($condition:expr) => {
            if $condition {
                return Err(Error::InvalidInput);
            }
        };
    }
    while cursor < bytecode.len() / 4 {
        let instruction = word(cursor);
        cursor += 1;
        let opcode = instruction & 0xffff;
        if opcode == 0xffff {
            reject!(
                instruction != 0xffff
                    || cursor != bytecode.len() / 4
                    || output_written != 15
                    || !sampled
            );
            return Ok(body);
        }
        if opcode == 0xfffe {
            reject!(instruction & 0x8000_0000 != 0);
            let count = ((instruction >> 16) & 0x7fff) as usize;
            reject!(count > bytecode.len() / 4 - cursor);
            cursor += count;
            continue;
        }
        instructions += 1;
        reject!(instructions > 512 || instruction & 0xf0ff0000 != 0);
        let count = ((instruction >> 24) & 15) as usize;
        reject!(count > bytecode.len() / 4 - cursor);
        let first = cursor;
        cursor += count;
        if opcode == 0 {
            reject!(count != 0);
            continue;
        }
        if opcode == 31 {
            reject!(count != 2 || executable);
            let declaration = word(first);
            let destination = word(first + 1);
            reject!(!parameter(destination) || destination & 0x0ff00000 != 0);
            let kind = register(destination);
            let index = destination & 0x7ff;
            let mask = (destination >> 16) & 15;
            if !model3
                && kind == 3
                && index == 0
                && declaration == 0x80000000
                && mask != 0
                && input_mask == 0
            {
                input_mask = mask;
            } else if model3
                && kind == 1
                && index < 10
                && declaration == 0x80000005
                && mask == 3
                && input_mask == 0
            {
                input_register = index;
                input_mask = mask;
                body.push_str(&format!("let input_v{index} = t0;\n"));
            } else if kind == 10
                && index == source_sampler
                && declaration == 0x90000000
                && mask == 15
                && !sampler_declared
            {
                sampler_declared = true;
            } else {
                return Err(Error::InvalidInput);
            }
            continue;
        }
        if opcode == 81 {
            reject!(count != 5 || executable);
            let destination = word(first);
            let index = (destination & 0x7ff) as usize;
            reject!(
                !parameter(destination)
                    || register(destination) != 2
                    || destination & 0x0fff0000 != 0x000f0000
                    || index >= 32
                    || definitions[index]
            );
            definitions[index] = true;
            let mut parts = Vec::with_capacity(4);
            for lane in 0..4 {
                let bits = word(first + lane + 1);
                reject!(!f32::from_bits(bits).is_finite());
                definition_bits[index][lane] = bits;
                parts.push(format!("bitcast<f32>({bits}u)"));
            }
            body.push_str(&format!("c[{index}] = vec4<f32>({});\n", parts.join(", ")));
            continue;
        }
        executable = true;
        let matrix = (20..=24).contains(&opcode);
        let columns = if opcode <= 21 { 4 } else { 3 };
        let rows = if opcode == 20 || opcode == 22 {
            4
        } else if opcode == 24 {
            2
        } else {
            3
        };
        let sources = match opcode {
            1 | 15 | 19 | 35 | 36 => 1,
            37 => {
                if model3 {
                    1
                } else {
                    3
                }
            }
            2 | 3 | 5 | 8 | 9 | 10 | 11 | 20..=24 | 33 | 66 => 2,
            4 | 18 | 88 | 90 => 3,
            _ => return Err(Error::InvalidInput),
        };
        reject!(count != sources + 1);
        let destination = word(first);
        let destination_type = register(destination);
        let destination_index = (destination & 0x7ff) as usize;
        let mask = (destination >> 16) & 15;
        let modifier = (destination >> 20) & 15;
        reject!(
            !parameter(destination)
                || destination & 0x0f000000 != 0
                || mask == 0
                || modifier > 1
                || !((destination_type == 0 && destination_index < 12)
                    || (destination_type == 8 && destination_index == 0))
        );
        reject!(model3 && opcode == 66 && destination_type != 0);
        reject!(matches!(opcode, 36 | 37) && destination_type != 0);
        reject!(opcode == 33 && (destination_type != 0 || mask & 8 != 0));
        reject!(opcode == 37 && mask > 3);
        reject!(matrix && mask != (1 << rows) - 1);
        let mut operands: [String; 5] = Default::default();
        let mut cross: [[String; 3]; 2] = Default::default();
        let (mut constant_register, mut constant_reads, mut texture_reads) = (32u32, 0u32, 0u32);
        let operand_count = if matrix { rows + 1 } else { sources };
        for index in 0..operand_count {
            let token = word(first + if matrix && index > 1 { 2 } else { index + 1 });
            let kind = register(token);
            let number = (token & 0x7ff)
                + if matrix && index > 0 {
                    (index - 1) as u32
                } else {
                    0
                };
            let source_modifier = (token >> 24) & 15;
            reject!(!parameter(token));
            if matrix {
                reject!(kind == destination_type && number as usize == destination_index);
                if index == 0 {
                    reject!(source_modifier > 1);
                } else {
                    reject!(
                        !matches!(kind, 0 | 2)
                            || source_modifier != 0
                            || (token >> 16) & 255 != 0xe4
                    );
                    let vector = word(first + 1);
                    constant_register = if register(vector) == 2 {
                        vector & 0x7ff
                    } else {
                        32
                    };
                    constant_reads = u32::from(constant_register != 32);
                }
            }
            if opcode == 37 && !model3 && index != 0 {
                reject!(
                    kind != 2
                        || number >= 32
                        || !definitions[number as usize]
                        || source_modifier != 0
                        || (token >> 16) & 255 != 0xe4
                );
                reject!(definition_bits[number as usize] != SINCOS[index - 1].map(f32::to_bits));
                continue;
            }
            if opcode == 66 && index == 1 {
                reject!(
                    kind != 10
                        || number != source_sampler
                        || !sampler_declared
                        || source_modifier != 0
                        || (token >> 16) & 255 != 0xe4
                );
                continue;
            }
            reject!(!matches!(source_modifier, 0 | 1 | 11 | 12));
            reject!(
                opcode == 33
                    && ((token >> 16) & 255 != 0xe4
                        || kind == destination_type && number as usize == destination_index)
            );
            let scalar = opcode == 15 || opcode == 37 || opcode == 90 && index == 2;
            reject!(scalar && (token >> 16) & 255 != ((token >> 16) & 3) * 0x55);
            if opcode == 36 {
                reject!(kind != 2 || number >= 32 || !definitions[number as usize]);
                let (mut nonzero, mut normal) = (false, false);
                for lane in 0..3 {
                    let selected = ((token >> (16 + 2 * lane)) & 3) as usize;
                    let magnitude =
                        f32::from_bits(definition_bits[number as usize][selected]).abs();
                    reject!(magnitude > f32::from_bits((127 + 62) << 23));
                    nonzero |= magnitude != 0.0;
                    normal |= magnitude >= f32::from_bits((127 - 62) << 23);
                }
                reject!(nonzero && !normal);
                if mask & 8 != 0 {
                    let magnitude = f32::from_bits(
                        definition_bits[number as usize][((token >> 22) & 3) as usize],
                    )
                    .abs();
                    reject!(
                        magnitude
                            > if nonzero {
                                f32::from_bits((127 + 62) << 23)
                            } else {
                                1.0
                            }
                    );
                }
            }
            reject!(model3 && opcode == 66 && (source_modifier != 0 || !matches!(kind, 0 | 1)));
            let (available, mut expression) = if kind == 0 && number < 12 {
                (written[number as usize], format!("r[{number}]"))
            } else if kind == 2 && number < 32 {
                reject!(constant_register != 32 && constant_register != number);
                if !model3 {
                    constant_reads += 1;
                    reject!(constant_reads > 2);
                }
                constant_register = number;
                (15, format!("c[{number}]"))
            } else if !model3 && kind == 3 && number == 0 {
                texture_reads += 1;
                reject!(texture_reads > 1);
                (input_mask, "t0".into())
            } else if model3 && kind == 1 && input_mask != 0 && number == input_register {
                (input_mask, format!("input_v{number}"))
            } else {
                return Err(Error::InvalidInput);
            };
            let cross_required = if mask & 1 != 0 { 6 } else { 0 }
                | if mask & 2 != 0 { 5 } else { 0 }
                | if mask & 4 != 0 { 3 } else { 0 };
            let required = if matrix {
                (1 << columns) - 1
            } else if scalar {
                1
            } else if opcode == 33 {
                cross_required
            } else if opcode == 36 {
                7 | (mask & 8)
            } else if opcode == 8 {
                7
            } else if opcode == 9 {
                15
            } else if opcode == 66 || opcode == 90 {
                3
            } else {
                mask
            };
            if opcode == 33 {
                for lane in 0..3 {
                    if required & (1 << lane) != 0 {
                        cross[index][lane] =
                            modified(format!("{expression}.{}", LANES[lane]), source_modifier);
                    }
                }
            }
            expression.push('.');
            for lane in 0..4 {
                let selected = ((token >> (16 + 2 * lane)) & 3) as usize;
                reject!(required & (1 << lane) != 0 && available & (1 << selected) == 0);
                expression.push(LANES[selected]);
            }
            operands[index] = modified(expression, source_modifier);
        }
        let [a, b, c, _, _] = &operands;
        let suffix = instructions;
        let mut result = match opcode {
            1 => a.clone(),
            2 => format!("{a} + {b}"),
            3 => format!("{a} - {b}"),
            4 => format!("{a} * {b} + {c}"),
            5 => format!("{a} * {b}"),
            8 => format!("vec4<f32>(dot(({a}).xyz, ({b}).xyz))"),
            9 => format!("vec4<f32>(dot({a}, {b}))"),
            10 => format!("min({a}, {b})"),
            11 => format!("max({a}, {b})"),
            15 => {
                body.push_str(&format!("let log_magnitude{suffix} = bitcast<u32>(({a}).x) & 2147483647u;\nvar log_result{suffix} = bitcast<f32>(4286578687u);\nif (log_magnitude{suffix} != 0u) {{\n  if (log_magnitude{suffix} < 8388608u) {{\n    log_result{suffix} = log2(f32(log_magnitude{suffix})) - 149.0;\n  }} else {{\n    log_result{suffix} = log2(bitcast<f32>(log_magnitude{suffix}));\n  }}\n}}\n"));
                format!("vec4<f32>(log_result{suffix})")
            }
            18 => format!("{a} * {b} + (vec4<f32>(1.0) - {a}) * {c}"),
            19 => format!("fract({a})"),
            20..=24 => {
                let components = if columns == 3 { ").xyz" } else { ")" };
                let parts = (0..4)
                    .map(|row| {
                        if row < rows {
                            format!("dot(({a}{components}, ({}{components})", operands[row + 1])
                        } else {
                            "0.0".into()
                        }
                    })
                    .collect::<Vec<_>>();
                format!("vec4<f32>({})", parts.join(", "))
            }
            33 => {
                let component = |first: usize, second: usize| {
                    format!(
                        "{} * {} - {} * {}",
                        cross[0][first], cross[1][second], cross[0][second], cross[1][first]
                    )
                };
                format!(
                    "vec4<f32>({}, {}, {}, 0.0)",
                    if mask & 1 != 0 {
                        component(1, 2)
                    } else {
                        "0.0".into()
                    },
                    if mask & 2 != 0 {
                        component(2, 0)
                    } else {
                        "0.0".into()
                    },
                    if mask & 4 != 0 {
                        component(0, 1)
                    } else {
                        "0.0".into()
                    }
                )
            }
            35 => format!("abs({a})"),
            36 => {
                body.push_str(&format!("let normal_source{suffix} = {a};\nlet normal_squared{suffix} = dot(normal_source{suffix}.xyz, normal_source{suffix}.xyz);\nvar normal_factor{suffix} = bitcast<f32>(2139095039u);\nif (normal_squared{suffix} != 0.0) {{\n  normal_factor{suffix} = inverseSqrt(normal_squared{suffix});\n}}\n"));
                format!(
                    "vec4<f32>(normal_source{suffix}.xyz * normal_factor{suffix}, {})",
                    if mask & 8 != 0 {
                        format!("normal_source{suffix}.w * normal_factor{suffix}")
                    } else {
                        "0.0".into()
                    }
                )
            }
            37 => {
                body.push_str(&format!("let angle{suffix} = ({a}).x;\n"));
                format!(
                    "vec4<f32>({}, {}, 0.0, 0.0)",
                    if mask & 1 != 0 {
                        format!("cos(angle{suffix})")
                    } else {
                        "0.0".into()
                    },
                    if mask & 2 != 0 {
                        format!("sin(angle{suffix})")
                    } else {
                        "0.0".into()
                    }
                )
            }
            66 => {
                sampled = true;
                format!("wpf_sample_input(({a}).xy)")
            }
            88 => format!("select({c}, {b}, {a} >= vec4<f32>(0.0))"),
            90 => format!("vec4<f32>(dot(({a}).xy, ({b}).xy) + ({c}).x)"),
            _ => return Err(Error::InvalidInput),
        };
        if modifier == 1 {
            result = format!("clamp({result}, vec4<f32>(0.0), vec4<f32>(1.0))");
        }
        body.push_str(&format!("let value{suffix} = {result};\n"));
        let target = if destination_type == 8 {
            "o".into()
        } else {
            format!("r[{destination_index}]")
        };
        for (lane, ch) in LANES.iter().enumerate() {
            if mask & (1 << lane) != 0 {
                body.push_str(&format!("{target}.{ch} = value{suffix}.{ch};\n"));
            }
        }
        if destination_type == 8 {
            output_written |= mask;
        } else {
            if opcode == 37 && !model3 {
                written[destination_index] &= 8;
            }
            written[destination_index] |= mask;
        }
    }
    Err(Error::InvalidInput)
}
