-- Crear tabla de clientes
CREATE TABLE IF NOT EXISTS public.clients (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(150),
    active BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Crear tabla de credenciales de Deriv
CREATE TABLE IF NOT EXISTS public.client_deriv_credentials (
    id SERIAL PRIMARY KEY,
    client_id INTEGER NOT NULL REFERENCES public.clients(id) ON DELETE CASCADE,
    app_id_real VARCHAR(50),
    api_token_real VARCHAR(100),
    app_id_virtual VARCHAR(50),
    api_token_virtual VARCHAR(100),
    UNIQUE(client_id)
);

-- Insertar cliente inicial Alexander si no existe
INSERT INTO public.clients (name, email)
SELECT 'Alexander', 'alexander@example.com'
WHERE NOT EXISTS (SELECT 1 FROM public.clients WHERE name = 'Alexander');

-- Insertar credenciales vacías para Alexander
INSERT INTO public.client_deriv_credentials (client_id)
SELECT id FROM public.clients WHERE name = 'Alexander'
ON CONFLICT (client_id) DO NOTHING;
