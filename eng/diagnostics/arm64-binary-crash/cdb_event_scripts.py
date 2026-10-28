"""Generate bounded native-fault callbacks; all counters are debugger-local."""
from pathlib import Path

# Explicit named faults are not covered by CDB's wildcard. Debugger-protocol
# notifications retain their default handling; CLR managed exceptions are excluded.
FAULTS = ('asrt','av','dm','dz','0xc000008e','eh','gp','ii','iov','ip',
          'isc','lsq','sbo','sov','wkd','aph','3c','ch','svh','vs','rto','rtt','*')

def quoted_command_path(path):
    # SX parses its command string once; preserve literal Windows separators.
    return str(path).replace(chr(92), chr(92) * 2)

def prepare(dest):
    dest=Path(dest).resolve()
    # Callback commands use unquoted script/file paths. Fail closed before launch.
    if any(c.isspace() or c in '\";{}' for c in str(dest)):
        raise ValueError('Debugger evidence path contains command syntax or whitespace.')
    scripts=[]
    for chance in (1,2):
        lines=['.if (@$t0 >= 8) { .echo PROGPU_CAPTURE_LIMIT; q } .else {',
               f'.printf "PROGPU_NATIVE_EVENT %d {chance} %08x\\n", @$t0, @$exr_code;',
               '.lastevent; .exr -1;']
        for i in range(8):
            lines.append(f'.if (@$t0 == {i}) {{ .dump /m {dest / f"event-{i:02}.dmp"}; }};')
        lines += ['r $t0 = @$t0 + 1;', 'gN;', '}']
        f=dest/f'event-chance-{chance}.cmd';f.write_text('\n'.join(lines)+'\n',encoding='utf-8');scripts.append(f)
    commands=['r $t0 = 0']
    for event in FAULTS:
        commands.append(f'sxe -c "$$><{quoted_command_path(scripts[0])}" -c2 "$$><{quoted_command_path(scripts[1])}" {event}')
    commands += ['sxi -c "" -c2 "" 0xe0434352',
                 'sxe -c ".echo PROGPU_TARGET_EXIT; .lastevent; q" epr',
                 '.echo PROGPU_FILTERS_BEGIN', 'sx', '.echo PROGPU_FILTERS_END', 'g']
    setup=dest/'capture-setup.cmd';setup.write_text('\n'.join(commands)+'\n',encoding='utf-8')
    return setup


def validate_filter_inventory(text):
    import re
    blocks=re.findall(r'^PROGPU_FILTERS_BEGIN\r?\n(.*?)^PROGPU_FILTERS_END\r?$',text,re.MULTILINE|re.DOTALL)
    if len(blocks)!=1:raise ValueError('Missing unique actual debugger filter inventory.')
    entries=re.split(r'(?m)(?=^\s{0,3}\S+ - )',blocks[0])
    observed=[]
    for entry in entries:
        header=entry.splitlines()[0] if entry.splitlines() else ''
        match=re.match(r'^\s{0,3}(\S+) - .* - not handled$',header)
        if not match:continue
        key=match.group(1)
        # CLR managed exception excluded; hc is a continuation policy for ch.
        if key in ('clr','hc'):continue
        if key not in FAULTS:raise ValueError('Uncovered named native fault: '+key)
        if 'event-chance-1.cmd' not in entry or 'event-chance-2.cmd' not in entry:
            raise ValueError('Missing native fault callback: '+key)
        observed.append(key)
    if not {'*','av','sbo','eh','svh','vs','rto','rtt'} <= set(observed):
        raise ValueError('Incomplete native fault inventory.')
    return observed
