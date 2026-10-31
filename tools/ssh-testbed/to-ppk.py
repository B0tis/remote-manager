"""Converts the testbed's unencrypted OpenSSH ed25519 key to PuTTY's .ppk (v2) so Pageant can load it.
Test tooling only: python to-ppk.py <id_ed25519> <out.ppk>"""
import base64, hashlib, hmac, struct, sys


def read_string(data, offset):
    (length,) = struct.unpack('>I', data[offset:offset + 4])
    return data[offset + 4:offset + 4 + length], offset + 4 + length


def string(value):
    return struct.pack('>I', len(value)) + value


def lines(blob):
    text = base64.b64encode(blob).decode()
    return [text[i:i + 64] for i in range(0, len(text), 64)]


source, target = sys.argv[1], sys.argv[2]
body = ''.join(l for l in open(source).read().splitlines() if not l.startswith('-----'))
data = base64.b64decode(body)
assert data.startswith(b'openssh-key-v1\0')
offset = len(b'openssh-key-v1\0')
cipher, offset = read_string(data, offset)
assert cipher == b'none', 'only unencrypted keys'
_, offset = read_string(data, offset)  # kdf name
_, offset = read_string(data, offset)  # kdf options
offset += 4  # number of keys
public_blob, offset = read_string(data, offset)
private_section, offset = read_string(data, offset)
p = 8  # two check ints
key_type, p = read_string(private_section, p)
assert key_type == b'ssh-ed25519'
_, p = read_string(private_section, p)  # public key
secret, p = read_string(private_section, p)  # 64 bytes: seed + public
comment, p = read_string(private_section, p)
private_blob = string(secret[:32])

mac_key = hashlib.sha1(b'putty-private-key-file-mac-key').digest()
mac_data = string(b'ssh-ed25519') + string(b'none') + string(comment) + string(public_blob) + string(private_blob)
mac = hmac.new(mac_key, mac_data, hashlib.sha1).hexdigest()
public_lines, private_lines = lines(public_blob), lines(private_blob)
with open(target, 'w', newline='\n') as out:
    out.write('PuTTY-User-Key-File-2: ssh-ed25519\nEncryption: none\n')
    out.write(f'Comment: {comment.decode()}\n')
    out.write(f'Public-Lines: {len(public_lines)}\n' + '\n'.join(public_lines) + '\n')
    out.write(f'Private-Lines: {len(private_lines)}\n' + '\n'.join(private_lines) + '\n')
    out.write(f'Private-MAC: {mac}\n')
