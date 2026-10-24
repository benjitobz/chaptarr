import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import { sizes } from 'Helpers/Props';
import ImportListItemsModalContent from './ImportListItemsModalContent';

function ImportListItemsModal(props) {
  const {
    isOpen,
    onModalClose,
    ...otherProps
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      size={sizes.LARGE}
      onModalClose={onModalClose}
    >
      {
        isOpen ?
          <ImportListItemsModalContent
            {...otherProps}
            onModalClose={onModalClose}
          /> :
          null
      }
    </Modal>
  );
}

ImportListItemsModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ImportListItemsModal;
