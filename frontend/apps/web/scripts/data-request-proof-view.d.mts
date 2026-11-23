export type DataRequestProofView = Readonly<{
  kind: 'data_request_document';
  view: string | null;
  filingForms: number;
  actionForms: number;
  downloadForms: number;
  confirmationProtected: boolean;
  historyProtected: boolean;
}>;
export function dataRequestProofView(html: string, policy?: string): DataRequestProofView;
