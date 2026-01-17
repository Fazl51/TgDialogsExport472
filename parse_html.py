from bs4 import BeautifulSoup
from pathlib import Path
import sys
path = Path(sys.argv[1])
sys.stdout.reconfigure(encoding='utf-8')
soup = BeautifulSoup(path.read_text(encoding='utf-8', errors='ignore'), 'html.parser')
print(soup.get_text('\n'))
