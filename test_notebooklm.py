from notebooklm_mcp.api_client import NotebookLMClient
from notebooklm_mcp.auth import load_cached_tokens

try:
    tokens = load_cached_tokens()
    if not tokens:
        print("No hay tokens guardados.")
    else:
        client = NotebookLMClient(cookies=tokens.cookies, csrf_token=tokens.csrf_token)
        notebooks = client.list_notebooks()
        print("Conexión exitosa. Tus Notebooks:")
        for idx, nb in enumerate(notebooks, start=1):
            title = getattr(nb, 'title', 'Notebook Sin Título')
            print(f"{idx}. {title}")
except Exception as e:
    print(f"Error al verificar libretas: {e}")
