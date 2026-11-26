"""Read-only file classification. Optional olefile for CFB checks; not a fidelity verifier."""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import zipfile

CFB = bytes.fromhex('D0 CF 11 E0 A1 B1 1A E1')

def inspect(path: Path) -> dict:
    result = {'path': str(path), 'format': 'unknown', 'structuralCheck': False}
    with path.open('rb') as f:
        head = f.read(8)
    if head == CFB:
        result['format'] = 'cfb_candidate'
        try:
            import olefile
        except ImportError:
            result['note'] = 'Optional olefile missing; HWP header not checked'
            return result
        with olefile.OleFileIO(str(path)) as ole:
            if ole.exists('FileHeader'):
                with ole.openstream('FileHeader') as stream:
                    header = stream.read(256)
                if (len(header) == 256 and header[:32].rstrip(b'\x00') == b'HWP Document File'
                        and header[35] == 5 and ole.exists('DocInfo')):
                    result.update(format='hwp5_candidate', structuralCheck=True,
                                  note='CFB, FileHeader, version and DocInfo checked; content not validated')
        return result
    if head[:4] in (b'PK\x03\x04', b'PK\x05\x06', b'PK\x07\x08'):
        result['format'] = 'zip_candidate'
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            if len(names) != len(set(names)):
                result['note'] = 'Duplicate ZIP entries'; return result
            if 'mimetype' in names and z.getinfo('mimetype').file_size <= 256:
                with z.open('mimetype') as stream:
                    mime = stream.read(257).strip()
                if (mime == b'application/hwp+zip' and 'Contents/header.xml' in names
                        and any(n.startswith('Contents/section') and n.endswith('.xml') for n in names)):
                    result.update(format='hwpx_candidate', structuralCheck=True,
                                  note='ZIP mimetype and key entry names checked; content not validated')
        return result
    return result

def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('file', type=Path)
    args = p.parse_args()
    try:
        result = inspect(args.file)
    except Exception as exc:
        print(json.dumps({'format': 'error', 'error': str(exc)}, ensure_ascii=False))
        return 5
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result['structuralCheck'] else 6

if __name__ == '__main__':
    raise SystemExit(main())
