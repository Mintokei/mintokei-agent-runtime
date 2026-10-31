"""Real Mem0 OSS adapter. infer=False; Codex remains the only LLM provider."""
import os
import sqlite3
import threading
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from mem0 import Memory
os.makedirs('/data', exist_ok=True)
url = os.getenv('OLLAMA_URL', 'http://ollama:11434')
memory = Memory.from_config({
    'vector_store': {'provider': 'qdrant', 'config': {'path': '/data/qdrant', 'collection_name': 'playground', 'embedding_model_dims': 768}},
    'embedder': {'provider': 'ollama', 'config': {'model': 'nomic-embed-text', 'ollama_base_url': url}},
    'llm': {'provider': 'ollama', 'config': {'model': 'unused-no-inference', 'ollama_base_url': url}},
    'history_db_path': '/data/history.db',
})
def no_inference(*args, **kwargs):
    raise RuntimeError('Mem0 inference is disabled. Use Codex through the application.')
memory.llm.generate_response = no_inference
mapping = sqlite3.connect('/data/identities.db', check_same_thread=False)
mapping.execute('CREATE TABLE IF NOT EXISTS identities (domain_id TEXT PRIMARY KEY, provider_id TEXT NOT NULL, owner TEXT NOT NULL)')
lock = threading.RLock()
app = FastAPI()
class Write(BaseModel):
    id: str
    text: str
    userId: str
    ns: str
    validFrom: str
class Search(BaseModel):
    query: str
    userId: str
    ns: str
    limit: int = 8
@app.get('/health')
def health(): return {'provider': 'mem0-oss', 'infer': False, 'embeddings': 'ollama:nomic-embed-text'}
@app.post('/memories')
def write(x: Write):
    owner = x.userId + ':' + x.ns
    with lock:
        row = mapping.execute('SELECT provider_id,owner FROM identities WHERE domain_id=?', (x.id,)).fetchone()
        if row:
            if row[1] != owner: raise HTTPException(403, 'Owner mismatch')
            memory.update(row[0], x.text)
            return {'id': row[0]}
        found = memory.get_all(user_id=owner)
        existing = next((r for r in found.get('results', []) if (r.get('metadata') or {}).get('domainId') == x.id), None)
        if existing: provider_id = existing['id']
        else:
            result = memory.add(x.text, user_id=owner, infer=False, metadata={'domainId': x.id, 'validFrom': x.validFrom})
            provider_id = result['results'][0]['id']
        mapping.execute('INSERT INTO identities VALUES (?,?,?)', (x.id, provider_id, owner))
        mapping.commit()
        return {'id': provider_id}
@app.post('/search')
def search(x: Search):
    with lock:
        result = memory.search(x.query, user_id=x.userId + ':' + x.ns, limit=min(x.limit, 50))
        items = []
        for r in result.get('results', []):
            row = mapping.execute('SELECT domain_id FROM identities WHERE provider_id=? AND owner=?', (r['id'], x.userId + ':' + x.ns)).fetchone()
            if row: items.append({'domainId': row[0], 'score': r.get('score')})
        return items
@app.delete('/memories/{domain_id}')
def delete(domain_id: str, owner: str):
    with lock:
        row = mapping.execute('SELECT provider_id,owner FROM identities WHERE domain_id=?', (domain_id,)).fetchone()
        if not row: return {'deleted': True}
        if row[1] != owner: raise HTTPException(403, 'Owner mismatch')
        memory.delete(row[0])
        mapping.execute('DELETE FROM identities WHERE domain_id=?', (domain_id,))
        mapping.commit()
        return {'deleted': True}
