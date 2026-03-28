from notebooklm_mcp.api_client import NotebookLMClient
from notebooklm_mcp.auth import load_cached_tokens

tokens = load_cached_tokens()
client = NotebookLMClient(cookies=tokens.cookies, csrf_token=tokens.csrf_token)
nb = client.create_notebook("trading")
if nb:
    print(f"Notebook creado: {nb.id}")
else:
    print("Error creando.")
