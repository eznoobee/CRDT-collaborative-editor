export { Replica } from './replica';
export type { ElementId } from './elementId';
export { compareElementId, elementIdsEqual, elementKey } from './elementId';
export type { ReplicaId } from './replicaId';
export { compareReplicaId, formatReplicaId, parseReplicaId } from './replicaId';
export type { DeleteOperation, InsertOperation, Operation, Side } from './operation';
export { encodeOperation, decodeOperation } from './wire';
export type { ElementState, VersionVectorEntry } from './elementState';
export {
  BinaryFormatError,
  FORMAT_VERSION,
  encodeSnapshot,
  decodeSnapshot,
  encodeOperations,
  decodeOperations,
  encodeOperationBinary,
  decodeOperationBinary,
} from './binary';
export type { DecodedSnapshot } from './binary';
