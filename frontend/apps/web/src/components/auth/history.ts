// Fixed source only. Veil before a document enters the history cache; a restored
// document stays veiled until a complete, freshly admitted network navigation.
// Native documents always carry a fixed GET recovery target: reloading a POST
// result could replay a mutation. The inline guard independently closes the DOM
// attribute to the same finite screens; invalid attributes fall back to sign-in.
export const BFF_HISTORY_SOURCE = `(()=>{let leaving=false;addEventListener('pagehide',()=>{leaving=true;document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving){document.documentElement.style.visibility='hidden';const r=document.documentElement.getAttribute('data-bff-history-recovery');if(r===null){location.reload()}else{const allowed=['/','/studio','/studio/members','/studio/roles','/portal','/portal/profile','/api/auth/entry','/api/auth/signin'];location.replace(allowed.includes(r)||/^\\/studio\\/(?:members|roles)\\/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(r)?r:'/api/auth/signin')}}})})();`;
