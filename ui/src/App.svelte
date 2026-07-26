<script>
  import { Router } from '@roxi/routify';
  import routes from '../.routify/routes.default.js';
  import { urlRewrite } from './lib/base.js';
</script>

<!-- urlRewrite is [] for a root build, so this is a no-op there. Routify
     concatenates it with its own plugin rewrites rather than replacing them. -->
<Router {routes} {urlRewrite} />
