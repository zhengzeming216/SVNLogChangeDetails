import re

FINAL = r"C:\Users\station167\AppData\Local\Microsoft\VisualStudio\18.0_e5d6eb5b\Extensions\SvnMethodLens"
real = FINAL  # backslashes preserved as-is in json via doubling

for name in ("catalog.json", "manifest.json"):
    p = FINAL + "\\" + name
    t = open(p, "rb").read().decode("utf-8")
    json_escaped = real.replace("\\", "\\\\")
    new = re.sub(r'("extensionDir":")([^"]*)(")',
                 lambda m: m.group(1) + json_escaped + m.group(3),
                 t)
    open(p, "wb").write(new.encode("utf-8"))
    m = re.search(r'"extensionDir":"[^"]*"', new)
    print(name, "->", m.group(0))
