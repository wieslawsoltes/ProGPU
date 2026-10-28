"""Generate bounded native-fault callbacks; all counters are debugger-local."""
from pathlib import Path

# Explicit named faults are not covered by CDB's wildcard. Debugger-protocol
# notifications retain their default handling; CLR managed exceptions are excluded.
FAULTS = ('asrt','av','dm','dz','0xc000008e','eh','gp','ii','iov','ip',
          'isc','lsq','sbo','sov','wkd','aph','3c','ch','*')

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
        commands.append(f'sxe -c "$$><{scripts[0]}" -c2 "$$><{scripts[1]}" {event}')
    commands += ['sxi -c "" -c2 "" 0xe0434352',
                 'sxe -c ".echo PROGPU_TARGET_EXIT; .lastevent; q" epr',
                 '.echo PROGPU_FILTERS_BEGIN', 'sx', '.echo PROGPU_FILTERS_END', 'g']
    setup=dest/'capture-setup.cmd';setup.write_text('\n'.join(commands)+'\n',encoding='utf-8')
    return setup
