# Win11Debloat .reg dosyalarından WindowsAyarlari tablosunu (C#) üretir.
import json, re, sys, io, os
os.chdir(os.path.dirname(os.path.abspath(__file__)))
F = {x['FeatureId']: x for x in json.load(open('Features.json', encoding='utf-8-sig'))['Features']}

def oku(yol):
    b = open(yol, 'rb').read()
    if b[:2] in (b'\xff\xfe', b'\xfe\xff'): return b.decode('utf-16')
    return b.decode('utf-8-sig')

def ayristir(yol):
    islemler, anahtar = [], None
    for satir in oku(yol).replace('\r', '').split('\n'):
        s = satir.strip()
        if not s or s.startswith(';') or s.startswith('Windows Registry'): continue
        m = re.match(r'^\[(-?)(.+)\]$', s)
        if m:
            sil, tam = m.group(1) == '-', m.group(2)
            anahtar = kok_ayir(tam)
            if anahtar and sil: islemler.append(('AnahtarSil', anahtar[0], anahtar[1], None, None))
            if sil: anahtar = None
            continue
        if anahtar is None: continue
        m = re.match(r'^(@|"((?:[^"\\]|\\.)*)")=(.*)$', s)
        if not m: raise Exception('anlaşılmadı: ' + yol + ': ' + s)
        ad = '' if m.group(1) == '@' else m.group(2).replace('\\\\', '\\').replace('\\"', '"')
        d = m.group(3)
        if d == '-': islemler.append(('DegerSil', anahtar[0], anahtar[1], ad, None))
        elif d.startswith('dword:'): islemler.append(('Dw', anahtar[0], anahtar[1], ad, int(d[6:], 16)))
        elif d.startswith('hex:'): islemler.append(('Hex', anahtar[0], anahtar[1], ad, d[4:]))
        elif d.startswith('"'): islemler.append(('Sz', anahtar[0], anahtar[1], ad, d[1:-1].replace('\\\\', '\\').replace('\\"', '"')))
        else: raise Exception('tür: ' + yol + ': ' + s)
    return islemler

def kok_ayir(tam):
    for on, kok in (('HKEY_CURRENT_USER\\', 'KU'), ('HKEY_LOCAL_MACHINE\\', 'LM'), ('HKEY_USERS\\S-1-5-20\\', 'AgHizmeti')):
        if tam.upper().startswith(on.upper()): return (kok, tam[len(on):])
    return None   # HKEY_CLASSES_ROOT vb.: alınmaz

def suz(islemler):
    # Edge ve OneDrive'a dokunan satırlar alınmaz (kullanıcı kararı).
    return [i for i in islemler if '\\microsoft\\edge' not in i[2].lower() and 'onedrive' not in i[2].lower()]

def cs(s): return '@"' + s.replace('"', '""') + '"'

def islem_cs(i):
    tur, kok, yol, ad, d = i
    k = 'Kok.' + kok
    if tur == 'AnahtarSil': return 'R.AnahtarSil(%s, %s)' % (k, cs(yol))
    if tur == 'DegerSil': return 'R.DegerSil(%s, %s, %s)' % (k, cs(yol), cs(ad))
    if tur == 'Dw': return 'R.Dw(%s, %s, %s, %d)' % (k, cs(yol), cs(ad), d)
    if tur == 'Sz': return 'R.Sz(%s, %s, %s, %s)' % (k, cs(yol), cs(ad), cs(d))
    if tur == 'Hex': return 'R.Hex(%s, %s, %s, "%s")' % (k, cs(yol), cs(ad), d)

# (kimlik, kategori, başlık, açıklama, öneri, etki, yalnızKU)  etki: "" | "gezgin" | "oturum"
A = json.load(open('ayarlar-tr.json', encoding='utf-8'))
cikti = io.StringIO()
for a in A:
    fid = a['id']
    if fid == 'DisableWidgets':
        uyg = [('Dw', 'LM', r'SOFTWARE\Policies\Microsoft\Dsh', 'AllowNewsAndInterests', 0)]
        geri = [('DegerSil', 'LM', r'SOFTWARE\Policies\Microsoft\Dsh', 'AllowNewsAndInterests', None)]
        mn, mx = None, None
    else:
        f = F[fid]
        uyg = suz(ayristir('reg/' + f['RegistryKey']))
        u = f.get('RegistryUndoKey')
        geri = suz(ayristir(('undo/' if os.path.exists('undo/' + u) else 'reg/') + u)) if u else []
        if a.get('yalnizKU'):
            uyg = [i for i in uyg if i[1] == 'KU']; geri = [i for i in geri if i[1] == 'KU']
        mn, mx = f.get('MinVersion'), f.get('MaxVersion')
    if any(i[0] == 'AnahtarSil' for i in uyg): raise Exception('uygulamada anahtar silme: ' + fid)
    cikti.write('        new WinAyar(%s, %s, %s,\n            %s,\n            %s, %d, %d, Etki.%s,\n' % (
        cs(fid), cs(a['kategori']), cs(a['baslik']), cs(a['aciklama']), 'true' if a.get('oneri') else 'false',
        mn or 0, mx or 0, {'': 'Yok', 'gezgin': 'Gezgin', 'oturum': 'Oturum'}[a.get('etki', '')]))
    cikti.write('            new[] {\n' + ',\n'.join('                ' + islem_cs(i) for i in uyg) + ' },\n')
    cikti.write('            new[] {\n' + ',\n'.join('                ' + islem_cs(i) for i in geri) + ' }),\n' if geri else '            new RegIslem[0]),\n')
open('tablo.cs.txt', 'w', encoding='utf-8').write(cikti.getvalue())
print('tamam', len(A))
