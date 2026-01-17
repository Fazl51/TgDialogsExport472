from bs4 import BeautifulSoup
from pathlib import Path
import sys
path = Path(sys.argv[1])
soup = BeautifulSoup(path.read_text(encoding='utf-8', errors='ignore'), 'html.parser')
for i, code in enumerate(soup.find_all('code'), 1):
    print(f"--- code block {i} ---")
    print(code.get_text('\n'))
