// Fixed source only. Veil before a document enters the history cache; a restored
// document stays veiled until a complete, freshly admitted network navigation.
export const BFF_HISTORY_SOURCE = `(()=>{let leaving=false;addEventListener('pagehide',()=>{leaving=true;document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving){document.documentElement.style.visibility='hidden';location.reload()}})})();`;
