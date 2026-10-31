"""Regenerates the KeePass test databases in this folder.

Usage: python -I make_fixtures.py <pykeepass checkout>
Needs `pip install pykeepass` (4.x). pykeepass's own test databases are only used as format templates
(cipher, KDF, KDBX version); every group and entry is replaced, and the master password and key file are ours.
Test data only: the master password below protects nothing real.
"""
import hashlib, os, secrets, shutil, sys
from pathlib import Path
from pykeepass import PyKeePass

MASTER = "RemoteManager-Test"
here = Path(__file__).resolve().parent
templates = Path(sys.argv[1]) / "tests"

# Our own key file in the KeePass 2.0 XML format.
key = secrets.token_bytes(32)
hexkey = key.hex().upper()
(here / "fixture.keyx").write_text(
    '<?xml version="1.0" encoding="utf-8"?>\n<KeyFile>\n\t<Meta>\n\t\t<Version>2.0</Version>\n\t</Meta>\n\t<Key>\n'
    f'\t\t<Data Hash="{hashlib.sha256(key).hexdigest()[:8].upper()}">\n\t\t\t{" ".join(hexkey[i:i+8] for i in range(0, 64, 8))}\n\t\t</Data>\n\t</Key>\n</KeyFile>\n',
    encoding="utf-8")

variants = [
    # (output, template, template password, template key file, our key file)
    ("kdbx3-aes-aeskdf.kdbx", "test3.kdbx", "password", "test3.key", None),
    ("kdbx4-aes-argon2d.kdbx", "test4_aes.kdbx", "password", "test4.key", None),
    ("kdbx4-chacha20-argon2d.kdbx", "test4_chacha20.kdbx", "password", "test4.key", None),
    ("kdbx4-twofish-argon2d.kdbx", "test4_twofish.kdbx", "password", "test4.key", None),
    ("kdbx4-aes-aeskdf.kdbx", "test4_aeskdf.kdbx", "password", "test4.key", None),
    ("kdbx4-aes-argon2id.kdbx", "test4_argon2id.kdbx", "password", None, None),
    ("kdbx4-aes-uncompressed.kdbx", "test4_aes_uncompressed.kdbx", "password", None, None),
    ("kdbx4-keyfile.kdbx", "test4_chacha20.kdbx", "password", "test4.key", "fixture.keyx"),
]

for out, template, password, keyfile, ourkey in variants:
    target = here / out
    shutil.copy(templates / template, target)
    kp = PyKeePass(str(target), password=password, keyfile=str(templates / keyfile) if keyfile else None)
    for entry in list(kp.root_group.entries):
        kp.delete_entry(entry)
    for group in list(kp.root_group.subgroups):
        kp.delete_group(group)
    kunden = kp.add_group(kp.root_group, "Kunden")
    kunde_a = kp.add_group(kunden, "Kunde A")
    server = kp.add_entry(kunde_a, "Server 01", "admin", "Pa55-äöü-€")
    kp.add_entry(kp.root_group, "Router", "root", "router-secret", url="https://router.example")
    kp.add_entry(kunden, "Doppelt", "a", "first")
    kp.add_entry(kunde_a, "Doppelt", "b", "second")
    # In the recycle bin: must not be found.
    kp.trash_entry(kp.add_entry(kp.root_group, "Gelöscht", "x", "deleted-secret"))
    # History with protected values before the current one: the inner stream must be consumed in order.
    server.save_history()
    server.password = "Pa55-äöü-€"
    server.set_custom_property("PIN", "4711", protect=True)
    kp.password = MASTER
    kp.keyfile = str(here / ourkey) if ourkey else None
    kp.save()
    # Reopen to prove the file is valid for pykeepass too.
    PyKeePass(str(target), password=MASTER, keyfile=str(here / ourkey) if ourkey else None)
    print(out, os.path.getsize(target), kp.version, kp.encryption_algorithm, kp.kdf_algorithm)
