import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BookCover from 'Book/BookCover';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TablePager from 'Components/Table/TablePager';
import TableRow from 'Components/Table/TableRow';
import { kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import titleCase from 'Utilities/String/titleCase';
import translate from 'Utilities/String/translate';
import styles from './ImportListItemsModalContent.css';

const PAGE_SIZE = 20;

const STATE_KINDS = {
  downloaded: kinds.SUCCESS,
  queued: kinds.PURPLE,
  missing: kinds.WARNING,
  unmonitored: kinds.DISABLED,
  excluded: kinds.DANGER,
  pendingAuthor: kinds.INFO,
  notInLibrary: kinds.DEFAULT,
  unknown: kinds.DEFAULT
};

const STATE_LABELS = {
  downloaded: 'Downloaded',
  queued: 'Queued',
  missing: 'Missing',
  unmonitored: 'Unmonitored',
  excluded: 'Excluded',
  pendingAuthor: 'PendingAuthor',
  notInLibrary: 'NotInLibrary',
  unknown: 'Unknown'
};

const MEDIA_TYPE_LABELS = {
  ebook: 'Ebook',
  audiobook: 'Audiobook'
};

function normalizeState(state) {
  if (!state) {
    return 'unknown';
  }

  const key = state.charAt(0).toLowerCase() + state.slice(1);

  return STATE_LABELS[key] ? key : 'unknown';
}

function stateLabel(state) {
  return translate(STATE_LABELS[normalizeState(state)]);
}

function mediaTypeLabel(mediaType) {
  const key = (mediaType || '').toLowerCase();
  const translationKey = MEDIA_TYPE_LABELS[key];

  return translationKey ? titleCase(translate(translationKey)) : titleCase(mediaType || '');
}

class ImportListItemsModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      page: 1,
      totalRecords: 0,
      records: [],
      isFetching: true,
      error: null
    };

    this._request = null;
  }

  componentDidMount() {
    this.fetchItems(1, false);
  }

  componentWillUnmount() {
    if (this._request) {
      this._request.abortRequest();
      this._request = null;
    }
  }

  //
  // Control

  fetchItems(page, refresh) {
    if (this._request) {
      this._request.abortRequest();
    }

    this.setState({ isFetching: true, error: null });

    const ajaxRequest = createAjaxRequest({
      url: `/importlist/${this.props.id}/items`,
      dataType: 'json',
      data: {
        page,
        pageSize: PAGE_SIZE,
        sortKey: 'dateAdded',
        sortDirection: 'descending',
        refresh
      }
    });

    this._request = ajaxRequest;

    ajaxRequest.request.then((data) => {
      if (this._request !== ajaxRequest) {
        return;
      }

      this._request = null;

      this.setState({
        page: data.page,
        totalRecords: data.totalRecords,
        records: data.records || [],
        isFetching: false,
        error: null
      });
    }, (xhr) => {
      if (xhr.aborted || this._request !== ajaxRequest) {
        return;
      }

      this._request = null;

      this.setState({
        isFetching: false,
        error: xhr
      });
    });
  }

  //
  // Listeners

  onFirstPagePress = () => {
    this.fetchItems(1, false);
  };

  onPreviousPagePress = () => {
    this.fetchItems(Math.max(1, this.state.page - 1), false);
  };

  onNextPagePress = () => {
    this.fetchItems(Math.min(this.getTotalPages(), this.state.page + 1), false);
  };

  onLastPagePress = () => {
    this.fetchItems(this.getTotalPages(), false);
  };

  onPageSelect = (page) => {
    this.fetchItems(page, false);
  };

  onRefreshPress = () => {
    this.fetchItems(this.state.page, true);
  };

  //
  // Render

  getTotalPages() {
    return Math.max(1, Math.ceil(this.state.totalRecords / PAGE_SIZE));
  }

  renderStates(item) {
    const media = item.media || [];

    if (!media.length || ['excluded', 'pendingAuthor', 'notInLibrary'].includes(normalizeState(item.state))) {
      const key = normalizeState(item.state);

      return (
        <Label
          kind={STATE_KINDS[key]}
          outline={key !== 'downloaded'}
          title={item.pendingError || undefined}
        >
          {stateLabel(item.state)}
        </Label>
      );
    }

    return media.map((entry) => {
      const key = normalizeState(entry.state);

      return (
        <Label
          key={`${entry.mediaType}-${entry.bookId}`}
          kind={STATE_KINDS[key]}
          outline={key !== 'downloaded'}
        >
          {`${mediaTypeLabel(entry.mediaType)}: ${stateLabel(entry.state)}`}
        </Label>
      );
    });
  }

  renderRow(item, index) {
    const {
      title,
      subtitle,
      author,
      images,
      bookId,
      releaseDate,
      dateAdded,
      bookProviderId,
      editionProviderId
    } = item;

    const key = `${bookProviderId || editionProviderId || title}-${index}`;
    const bookLink = bookId ? `/book/${bookId}` : null;

    const cover = (
      <BookCover
        className={styles.cover}
        images={images || []}
        size={70}
        lazy={false}
        overflow={true}
      />
    );

    return (
      <TableRow key={key}>
        <TableRowCell className={styles.coverCell}>
          {
            bookLink ?
              <Link
                className={styles.coverLink}
                to={bookLink}
                title={translate('ViewBook')}
              >
                {cover}
              </Link> :
              cover
          }
        </TableRowCell>

        <TableRowCell className={styles.titleCell}>
          <div className={styles.title}>
            {
              bookLink ?
                <Link
                  to={bookLink}
                  title={translate('ViewBook')}
                >
                  {title}
                </Link> :
                title
            }
          </div>

          {
            subtitle ?
              <div className={styles.subtitle}>{subtitle}</div> :
              null
          }

          {
            author ?
              <div className={styles.author}>{author}</div> :
              null
          }
        </TableRowCell>

        <TableRowCell className={styles.statusCell}>
          <div className={styles.states}>
            {this.renderStates(item)}
          </div>
        </TableRowCell>

        <RelativeDateCellConnector
          className={styles.dateCell}
          date={releaseDate}
          timeForToday={false}
        />

        <RelativeDateCellConnector
          className={styles.dateCell}
          date={dateAdded}
        />
      </TableRow>
    );
  }

  render() {
    const {
      name,
      onModalClose
    } = this.props;

    const {
      page,
      totalRecords,
      records,
      isFetching,
      error
    } = this.state;

    const columns = [
      {
        name: 'cover',
        label: '',
        isVisible: true,
        isSortable: false
      },
      {
        name: 'title',
        label: translate('Title'),
        isVisible: true,
        isSortable: false
      },
      {
        name: 'status',
        className: styles.leftHeader,
        label: translate('Status'),
        isVisible: true,
        isSortable: false
      },
      {
        name: 'releaseDate',
        className: styles.leftHeader,
        label: translate('ReleaseDate'),
        isVisible: true,
        isSortable: false
      },
      {
        name: 'dateAdded',
        className: styles.leftHeader,
        label: translate('DateAdded'),
        isVisible: true,
        isSortable: false
      }
    ];

    const totalPages = this.getTotalPages();
    const showEmpty = !isFetching && !error && !records.length;

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('ImportListItemsHeader', { name })}
        </ModalHeader>

        <ModalBody>
          {
            isFetching && !records.length ?
              <LoadingIndicator /> :
              null
          }

          {
            error ?
              <Alert kind={kinds.DANGER}>
                {translate('ImportListItemsLoadError')}
              </Alert> :
              null
          }

          {
            showEmpty ?
              <div className={styles.empty}>
                {translate('ImportListItemsEmpty')}
              </div> :
              null
          }

          {
            records.length ?
              <div>
                <Table columns={columns}>
                  <TableBody>
                    {records.map((item, index) => this.renderRow(item, index))}
                  </TableBody>
                </Table>

                <TablePager
                  page={page}
                  totalPages={totalPages}
                  totalRecords={totalRecords}
                  isFetching={isFetching}
                  onFirstPagePress={this.onFirstPagePress}
                  onPreviousPagePress={this.onPreviousPagePress}
                  onNextPagePress={this.onNextPagePress}
                  onLastPagePress={this.onLastPagePress}
                  onPageSelect={this.onPageSelect}
                />
              </div> :
              null
          }
        </ModalBody>

        <ModalFooter>
          <Button
            isDisabled={isFetching}
            onPress={this.onRefreshPress}
          >
            {translate('Refresh')}
          </Button>

          <Button onPress={onModalClose}>
            {translate('Close')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

ImportListItemsModalContent.propTypes = {
  id: PropTypes.number.isRequired,
  name: PropTypes.string.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default ImportListItemsModalContent;
